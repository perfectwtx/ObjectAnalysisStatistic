using System.Text;
using Microsoft.CodeAnalysis;

namespace ObjectAnalyzer.Reporting;

/// <summary>
/// Exports inferred object schemas based on analysis results to
/// C# record/class (via Roslyn SyntaxFactory AST), TypeScript interface, or JSON Schema (via System.Text.Json.Nodes DOM).
/// Correctly un-flattens dot-separated paths into nested DTO classes/interfaces and groups array indices into typed array/list fields.
/// </summary>
public static partial class SchemaExporter
{
    private sealed class SchemaNode
    {
        public string NodeName { get; set; } = "";
        public string Kind { get; set; } = "Primitive"; // "Primitive", "Object", "Array"
        public string CSharpBaseType { get; set; } = "string";
        public string TsBaseType { get; set; } = "string";
        public string JsonSchemaBaseType { get; set; } = "string";
        public string? Format { get; set; }
        public bool IsNullable { get; set; }
        public FieldStatistic? Field { get; set; }
        public Dictionary<string, SchemaNode> Properties { get; } = new(StringComparer.Ordinal);
        public SchemaNode? ElementNode { get; set; }
    }

    public static void Export(AnalysisResult result, string format, string outputPath)
    {
        string content = format.ToLowerInvariant() switch
        {
            "csharp" or "cs" => GenerateCSharp(result),
            "typescript" or "ts" => GenerateTypeScript(result),
            "json-schema" or "jsonschema" or "schema" => GenerateJsonSchema(result),
            "openapi" or "swagger" or "openapi-json" => GenerateOpenApiJson(result),
            "openapi-yaml" or "yaml-schema" => GenerateOpenApiYaml(result),
            "protobuf" or "proto" or "proto3" => GenerateProtobuf(result),
            "sql" or "postgres" or "postgresql" or "pg" => GenerateSql(result, "postgres", SqlTableNameFromPath(outputPath)),
            "mysql" => GenerateSql(result, "mysql", SqlTableNameFromPath(outputPath)),
            "sqlite" => GenerateSql(result, "sqlite", SqlTableNameFromPath(outputPath)),
            "sqlserver" or "mssql" => GenerateSql(result, "sqlserver", SqlTableNameFromPath(outputPath)),
            _ => throw new InvalidOperationException($"不支持的 Schema 导出格式: '{format}'。可用格式: csharp, typescript, json-schema, openapi, openapi-yaml, proto, sql (postgres/mysql/sqlite/sqlserver)")
        };

        File.WriteAllText(outputPath, content, Encoding.UTF8);
    }


    // ─────────────────────────────────────────────
    //  AST Tree Builder
    // ─────────────────────────────────────────────

    private static SchemaNode BuildSchemaTree(AnalysisResult result)
    {
        var root = new SchemaNode { NodeName = "Root", Kind = "Object" };

        foreach (var field in result.FieldStatistics)
        {
            var segments = FieldPath.Parse(field.FieldName).Segments;
            SchemaNode current = root;
            for (int i = 0; i < segments.Length; i++)
            {
                var segment = segments[i];
                if (segment.IsIndex)
                {
                    current.Kind = "Array";
                    current = current.ElementNode ??= new SchemaNode { NodeName = "Item" };
                }
                else
                {
                    current.Kind = "Object";
                    if (!current.Properties.TryGetValue(segment.Name!, out var child))
                    {
                        child = new SchemaNode { NodeName = segment.Name!, IsNullable = field.Coverage < 1 || field.NullAppeared };
                        current.Properties.Add(segment.Name!, child);
                    }
                    current = child;
                }
                if (i == segments.Length - 1) ApplyFieldType(current, field);
            }
        }

        return root;
    }

    /// <summary>
    /// 将叶节点字段的静态类型信息应用到 SchemaNode。
    /// 若节点已包含子属性（如先出现 "address.city" 又出现 "address"），保持对象形态，不做降级。
    /// </summary>
    private static void ApplyFieldType(SchemaNode node, FieldStatistic field)
    {
        node.IsNullable = field.NullAppeared;
        node.Field = field;

        switch (field.PrimaryType)
        {
            case "Object":
                node.Kind = "Object";
                break;

            case "Array":
                if (node.Properties.Count == 0)
                {
                    node.Kind = "Array";
                    node.ElementNode ??= new SchemaNode { NodeName = "Item", Kind = "Primitive" };
                    if (node.ElementNode.Properties.Count == 0 && node.ElementNode.Kind != "Object")
                    {
                        node.ElementNode.CSharpBaseType = "string";
                        node.ElementNode.TsBaseType = "string";
                        node.ElementNode.JsonSchemaBaseType = "string";
                        node.ElementNode.Format = null;
                    }
                }
                break;

            default:
                if (node.Properties.Count == 0)
                {
                    node.Kind = "Primitive";
                    node.CSharpBaseType = MapToCSharpBaseType(field);
                    node.TsBaseType = MapToTsBaseType(field);
                    node.JsonSchemaBaseType = MapToJsonSchemaBaseType(field, out string? fmt);
                    node.Format = fmt;
                }
                break;
        }
    }


    private static string SanitizeIdentifier(string fieldName)
    {
        string sanitized = new string(fieldName
            .Select(c => char.IsLetterOrDigit(c) ? c : '_')
            .ToArray());

        if (string.IsNullOrEmpty(sanitized)) return "Property";
        if (char.IsDigit(sanitized[0])) sanitized = "_" + sanitized;

        return char.ToUpperInvariant(sanitized[0]) + sanitized.Substring(1);
    }


}
