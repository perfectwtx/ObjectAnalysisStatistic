using System.Text.Json;
using System.Text.Json.Nodes;
using ObjectAnalyzer.Data;

namespace ObjectAnalyzer.Reporting;

public sealed class JsonSchemaBackend : ISchemaBackend
{
    public string Format => "json-schema";
    public SchemaExportResult Generate(SchemaDocument document, SchemaExportOptions options)
    {
        var context = new SchemaRenderContext(document, options);
        var root = SchemaJsonWriter.Node(document.Root, "", context);
        root["$schema"] = "https://json-schema.org/draft/2020-12/schema"; root["title"] = options.RootName;
        SchemaJsonWriter.Diagnostics(root, context);
        return context.Finish(root.ToJsonString(JsonSharedOptions.Write));
    }
}

internal static class SchemaJsonWriter
{
    internal static void Diagnostics(JsonObject root, SchemaRenderContext context)
    { if (context.Diagnostics.Length > 0) root["x-object-analyzer-diagnostics"] = JsonSerializer.SerializeToNode(context.Diagnostics); }
    internal static JsonObject Node(SchemaType type, string path, SchemaRenderContext context, SchemaNamePlan? names = null, bool inlineObject = false)
    {
        if (type.HasUnknownValues || type.Kinds.Length == 0)
        { context.Note("unknown-type", path, "Unknown evidence is emitted as an unconstrained schema."); return new(); }
        var variants = type.Kinds.Where(kind => kind != RecordValueKind.Null).Select(kind => Variant(type, kind, path, context, names, inlineObject)).ToList();
        JsonObject result;
        if (variants.Count == 0) result = new() { ["type"] = "null" };
        else if (variants.Count == 1)
        {
            result = variants[0];
            if (type.AllowsNull)
            {
                if (result["type"] is JsonValue scalar && scalar.TryGetValue<string>(out string? name)) result["type"] = new JsonArray(name, "null");
                else result = new() { ["anyOf"] = new JsonArray(result, new JsonObject { ["type"] = "null" }) };
            }
        }
        else
        {
            if (type.AllowsNull) variants.Add(new() { ["type"] = "null" });
            result = new() { ["anyOf"] = new JsonArray(variants.Select(item => (JsonNode)item).ToArray()) };
        }
        if (type.EnumValues.Length > 0) result["enum"] = new JsonArray(type.EnumValues.Select(value => JsonNode.Parse(value.ToJsonString())).ToArray());
        return result;
    }
    private static JsonObject Variant(SchemaType node, RecordValueKind kind, string path, SchemaRenderContext context, SchemaNamePlan? names, bool inlineObject)
    {
        if (kind == RecordValueKind.Object && names is not null && !inlineObject)
            return new() { ["$ref"] = "#/components/schemas/" + names.TypeName(node) };
        if (kind == RecordValueKind.Object)
        {
            var properties = new JsonObject(); var required = new JsonArray();
            foreach (var property in node.Properties.OrderBy(item => item.Name, StringComparer.Ordinal))
            {
                properties[property.Name] = Node(property.Type, SchemaRenderContext.Child(path, property.Name), context, names);
                if (property.Presence == SchemaPresence.Required) required.Add(property.Name);
            }
            return new() { ["type"] = "object", ["properties"] = properties, ["required"] = required, ["additionalProperties"] = true };
        }
        if (kind == RecordValueKind.Array)
        {
            if (node.Element is null) context.Note("array-element-unknown", path, "Array element type was not observed; items stays unconstrained.");
            return new() { ["type"] = "array", ["items"] = node.Element is null ? new JsonObject() : Node(node.Element, path + "[]", context, names) };
        }
        string type = kind == RecordValueKind.Boolean ? "boolean" : kind == RecordValueKind.Number ? node.Numeric?.Integral == true ? "integer" : "number" : "string";
        var result = new JsonObject { ["type"] = type };
        string? format = kind switch
        {
            RecordValueKind.DateTimeOffset => "date-time", RecordValueKind.Date => "date", RecordValueKind.Guid => "uuid",
            _ => node.VerifiedSemanticTypes.Length == 1 ? node.VerifiedSemanticTypes[0] switch { "DateTime" => "date-time", "UUID" => "uuid", "Email" => "email", "IPv4" => "ipv4", "URL" => "uri", _ => null } : null
        };
        if (kind is RecordValueKind.DateTime or RecordValueKind.Time or RecordValueKind.Duration)
            context.Note("json-local-temporal-format", path, "Local CLR date/time or duration text is retained as string; it is not asserted to be an RFC3339 timestamp or ISO8601 duration.");
        if (type == "string" && format is not null) result["format"] = format;
        if (kind == RecordValueKind.Binary) result["contentEncoding"] = "base64";
        if (kind == RecordValueKind.Number && node.Numeric is { } number)
        {
            if (number.Minimum is not null) result["x-observed-minimum"] = JsonNode.Parse(number.Minimum);
            if (number.Maximum is not null) result["x-observed-maximum"] = JsonNode.Parse(number.Maximum);
            result["x-range-exact"] = number.RangeExact;
        }
        return result;
    }
}
