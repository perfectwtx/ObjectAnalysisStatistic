using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ObjectAnalyzer.Reporting;

public static partial class SchemaExporter
{

    /// <summary>
    /// Generates TypeScript interface declarations, un-flattening nested objects and typed arrays.
    /// </summary>
    public static string GenerateTypeScript(AnalysisResult result, string interfaceName = "GeneratedSchema")
    {
        var rootTree = BuildSchemaTree(result);
        var sb = new StringBuilder();

        sb.AppendLine("/**");
        sb.AppendLine(" * 由 ObjectAnalyzer 自动生成的 TypeScript 接口声明");
        sb.AppendLine(" */");

        GenerateTypeScriptInterfaces(rootTree, interfaceName, sb);

        return sb.ToString();
    }

    // ─────────────────────────────────────────────
    //  TypeScript Generator
    // ─────────────────────────────────────────────

    private static string GenerateTypeScriptInterfaces(SchemaNode node, string interfaceName, StringBuilder sb)
    {
        // 递归先生成子 DTO 接口
        foreach (var propKvp in node.Properties)
        {
            var childNode = propKvp.Value;
            string sanitizedPropName = SanitizeIdentifier(propKvp.Key);

            if (childNode.Kind == "Object")
            {
                GenerateTypeScriptInterfaces(childNode, $"{sanitizedPropName}Dto", sb);
            }
            else if (childNode.Kind == "Array" && childNode.ElementNode?.Kind == "Object")
            {
                GenerateTypeScriptInterfaces(childNode.ElementNode, $"{sanitizedPropName}ItemDto", sb);
            }
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"export interface {interfaceName} {{");
        foreach (var propKvp in node.Properties)
        {
            string childName = propKvp.Key;
            var childNode = propKvp.Value;
            string sanitizedPropName = SanitizeIdentifier(childName);
            string optional = childNode.IsNullable ? "?" : "";
            string fieldType;

            if (childNode.Kind == "Object")
            {
                fieldType = $"{sanitizedPropName}Dto";
            }
            else if (childNode.Kind == "Array")
            {
                string elemType = childNode.ElementNode?.Kind == "Object"
                    ? $"{sanitizedPropName}ItemDto"
                    : (childNode.ElementNode?.TsBaseType ?? "string");
                fieldType = $"{elemType}[]";
            }
            else
            {
                fieldType = childNode.TsBaseType;
            }

            string formattedPropName = FormatTsPropertyName(childName);
            sb.AppendLine(CultureInfo.InvariantCulture, $"  {formattedPropName}{optional}: {fieldType};");
        }
        sb.AppendLine("}");
        sb.AppendLine();

        return interfaceName;
    }

    private static string MapToTsBaseType(FieldStatistic field)
    {
        if (field.SemanticType == "DateTime") return "string";
        if (field.PrimaryType == "Number") return "number";
        if (field.PrimaryType == "Boolean") return "boolean";
        if (field.PrimaryType == "Array") return "any[]";
        if (field.PrimaryType == "Object") return "Record<string, any>";
        return "string";
    }

    private static string FormatTsPropertyName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "\"\"";
        bool isValid = (char.IsLetter(name[0]) || name[0] == '_' || name[0] == '$') &&
                       name.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '$');
        return isValid ? name : $"\"{name}\"";
    }
}
