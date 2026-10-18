using System.Collections.Immutable;
using System.Text;

namespace ObjectAnalyzer.Reporting;

public sealed record SchemaExportOptions
{
    public string RootName { get; init; } = "GeneratedSchema";
    public string SqlDialect { get; init; } = "postgres";
    public bool Strict { get; init; }
}
public sealed record SchemaExportResult(string Content, ImmutableArray<SchemaDiagnostic> Diagnostics);
public interface ISchemaBackend
{
    string Format { get; }
    SchemaExportResult Generate(SchemaDocument document, SchemaExportOptions options);
}
public sealed class SchemaExportException(ImmutableArray<SchemaDiagnostic> diagnostics)
    : InvalidOperationException("The target cannot preserve all schema semantics; inspect Diagnostics or use non-strict export.")
{
    public ImmutableArray<SchemaDiagnostic> Diagnostics { get; } = diagnostics;
}

/// <summary>Compatibility facade. Inference, identifier allocation, and target rendering are separate operations.</summary>
public static class SchemaExporter
{
    public static SchemaExportResult Generate(SchemaDocument document, string format, SchemaExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        options ??= new();
        ISchemaBackend backend = format.ToLowerInvariant() switch
        {
            "csharp" or "cs" => new CSharpSchemaBackend(),
            "typescript" or "ts" => new TypeScriptSchemaBackend(),
            "json-schema" or "jsonschema" or "schema" => new JsonSchemaBackend(),
            "openapi" or "swagger" or "openapi-json" => new OpenApiSchemaBackend(),
            "openapi-yaml" or "yaml-schema" => new OpenApiSchemaBackend(yaml: true),
            "protobuf" or "proto" or "proto3" => new ProtobufSchemaBackend(),
            "sql" or "postgres" or "postgresql" or "pg" => new SqlSchemaBackend(),
            "mysql" or "sqlite" or "sqlserver" or "mssql" => new SqlSchemaBackend(),
            _ => throw new ArgumentException($"Unsupported schema backend: {format}", nameof(format))
        };
        if (format.ToLowerInvariant() is "mysql" or "sqlite" or "sqlserver" or "mssql") options = options with { SqlDialect = format };
        return backend.Generate(document, options);
    }
    public static SchemaExportResult Generate(AnalysisResult result, string format, SchemaExportOptions? options = null)
        => Generate(SchemaInference.Infer(result), format, options);
    public static string GenerateCSharp(AnalysisResult result, string className = "GeneratedSchema") => Generate(result, "csharp", new() { RootName = className }).Content;
    public static string GenerateTypeScript(AnalysisResult result, string interfaceName = "GeneratedSchema") => Generate(result, "typescript", new() { RootName = interfaceName }).Content;
    public static string GenerateJsonSchema(AnalysisResult result) => Generate(result, "json-schema").Content;
    public static string GenerateOpenApiJson(AnalysisResult result, string title = "GeneratedSchema") => Generate(result, "openapi", new() { RootName = title }).Content;
    public static string GenerateOpenApiYaml(AnalysisResult result, string title = "GeneratedSchema") => Generate(result, "openapi-yaml", new() { RootName = title }).Content;
    public static string GenerateProtobuf(AnalysisResult result, string messageName = "GeneratedSchema") => Generate(result, "proto", new() { RootName = messageName }).Content;
    public static string GenerateSql(AnalysisResult result, string dialect = "postgres", string tableName = "GeneratedSchema") => Generate(result, "sql", new() { RootName = tableName, SqlDialect = dialect }).Content;
    public static void Export(AnalysisResult result, string format, string outputPath)
    {
        string name = Path.GetFileNameWithoutExtension(outputPath);
        if (name.EndsWith("_schema", StringComparison.OrdinalIgnoreCase)) name = name[..^7];
        bool sql = format.ToLowerInvariant() is "sql" or "postgres" or "postgresql" or "pg" or "mysql" or "sqlite" or "sqlserver" or "mssql";
        var generated = Generate(result, format, new() { RootName = sql && name.Length > 0 ? name : "GeneratedSchema" });
        File.WriteAllText(outputPath, generated.Content, Encoding.UTF8);
    }
}

internal sealed class SchemaRenderContext(SchemaDocument document, SchemaExportOptions options)
{
    private readonly List<SchemaDiagnostic> _diagnostics = Check(document);
    private static List<SchemaDiagnostic> Check(SchemaDocument document)
    {
        if (document.Root.Kinds.Length != 1 || document.Root.Kinds[0] != RecordValueKind.Object || document.Root.HasUnknownValues)
            throw new ArgumentException("Schema exports require an object record root.", nameof(document));
        return document.Diagnostics.ToList();
    }
    public void Note(string code, string path, string message) => _diagnostics.Add(new(code, path, message));
    public ImmutableArray<SchemaDiagnostic> Diagnostics => _diagnostics.Distinct().OrderBy(item => item.Path, StringComparer.Ordinal).ThenBy(item => item.Code, StringComparer.Ordinal).ToImmutableArray();
    public SchemaExportResult Finish(string content)
    {
        if (options.Strict && Diagnostics.Length > 0) throw new SchemaExportException(Diagnostics);
        return new(content, Diagnostics);
    }
    public string Comments(string prefix) => string.Concat(Diagnostics.Select(item => prefix + " " + item.Code + " " + System.Text.Json.JsonSerializer.Serialize(item.Path) + ": " + item.Message + "\n"));
    public static string Child(string path, string name) => path + "[" + System.Text.Json.JsonSerializer.Serialize(name) + "]";
}
