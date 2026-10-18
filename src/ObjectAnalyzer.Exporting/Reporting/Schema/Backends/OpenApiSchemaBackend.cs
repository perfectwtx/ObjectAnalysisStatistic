using System.Text;
using System.Text.Json.Nodes;
using ObjectAnalyzer.Data;

namespace ObjectAnalyzer.Reporting;

public sealed class OpenApiSchemaBackend(bool yaml = false) : ISchemaBackend
{
    public string Format => yaml ? "openapi-yaml" : "openapi";
    public SchemaExportResult Generate(SchemaDocument document, SchemaExportOptions options)
    {
        var context = new SchemaRenderContext(document, options);
        var names = SchemaNamePlan.Create(document, SchemaNamingTarget.OpenApi, options.RootName);
        var schemas = new JsonObject();
        foreach (var node in names.Objects)
            schemas[names.TypeName(node)] = SchemaJsonWriter.Node(node with { Kinds = [RecordValueKind.Object] }, names.TypeName(node), context, names, inlineObject: true);
        var root = new JsonObject
        {
            ["openapi"] = "3.1.0", ["jsonSchemaDialect"] = "https://json-schema.org/draft/2020-12/schema",
            ["info"] = new JsonObject { ["title"] = options.RootName + " API Models", ["version"] = "1.0.0" },
            ["paths"] = new JsonObject(), ["components"] = new JsonObject { ["schemas"] = schemas }
        };
        SchemaJsonWriter.Diagnostics(root, context);
        return context.Finish(yaml ? Yaml(root) : root.ToJsonString(JsonSharedOptions.Write));
    }
    // JSON scalar syntax is also valid YAML 1.2. Quoting every unsafe key avoids YAML tags, aliases and multiline injection.
    private static string Yaml(JsonNode root)
    {
        var result = new StringBuilder(); Write(root, 0, result); return result.ToString();
    }
    private static bool Scalar(JsonNode? node) => node is null or JsonValue || node is JsonObject { Count: 0 } or JsonArray { Count: 0 };
    private static void Write(JsonNode? node, int indent, StringBuilder output)
    {
        string padding = new(' ', indent);
        if (node is JsonObject obj)
        {
            foreach (var pair in obj)
            {
                string key = pair.Key.Length > 0 && char.IsAsciiLetter(pair.Key[0]) && pair.Key.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_')
                    ? pair.Key : System.Text.Json.JsonSerializer.Serialize(pair.Key);
                output.Append(padding).Append(key).Append(':');
                if (Scalar(pair.Value)) output.Append(' ').AppendLine(pair.Value?.ToJsonString() ?? "null");
                else { output.AppendLine(); Write(pair.Value, indent + 2, output); }
            }
        }
        else if (node is JsonArray array)
            foreach (var item in array)
            {
                output.Append(padding).Append('-');
                if (Scalar(item)) output.Append(' ').AppendLine(item?.ToJsonString() ?? "null");
                else { output.AppendLine(); Write(item, indent + 2, output); }
            }
    }
}
