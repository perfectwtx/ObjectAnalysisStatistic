using System.Text.Json.Nodes;

namespace ObjectAnalyzer.Reporting;

public static partial class SchemaExporter
{

    /// <summary>
    /// Generates standard JSON Schema draft 2020-12 using System.Text.Json.Nodes DOM.
    /// </summary>
    public static string GenerateJsonSchema(AnalysisResult result)
    {
        var rootTree = BuildSchemaTree(result);

        var schemaRoot = new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["title"] = "GeneratedSchema",
            ["type"] = "object",
            ["properties"] = BuildJsonSchemaProperties(rootTree),
            ["required"] = BuildJsonSchemaRequired(rootTree)
        };

        return schemaRoot.ToJsonString(JsonSharedOptions.Write);
    }

    // ─────────────────────────────────────────────
    //  JSON Schema Generator
    // ─────────────────────────────────────────────

    private static JsonObject BuildJsonSchemaProperties(SchemaNode node)
    {
        var properties = new JsonObject();
        foreach (var pair in node.Properties) properties[pair.Key] = BuildJsonSchemaNode(pair.Value);
        return properties;
    }

    private static JsonObject BuildJsonSchemaNode(SchemaNode node)
    {
        string type = node.Kind switch { "Object" => "object", "Array" => "array", _ => node.JsonSchemaBaseType };
        var definition = new JsonObject();
        definition["type"] = node.IsNullable ? new JsonArray { type, "null" } : JsonValue.Create(type);
        if (node.Kind == "Object")
        {
            definition["properties"] = BuildJsonSchemaProperties(node);
            definition["required"] = BuildJsonSchemaRequired(node);
        }
        else if (node.Kind == "Array")
            definition["items"] = node.ElementNode is null ? new JsonObject() : BuildJsonSchemaNode(node.ElementNode);
        else if (node.Format is not null) definition["format"] = node.Format;
        return definition;
    }

    private static JsonArray BuildJsonSchemaRequired(SchemaNode node)
    {
        var reqArray = new JsonArray();
        foreach (var propKvp in node.Properties)
        {
            if (!propKvp.Value.IsNullable)
            {
                reqArray.Add(propKvp.Key);
            }
        }
        return reqArray;
    }

    private static string MapToJsonSchemaBaseType(FieldStatistic field, out string? format)
    {
        format = field.SemanticType switch
        {
            "DateTime" => "date-time",
            "UUID" => "uuid",
            "Email" => "email",
            "IPv4" => "ipv4",
            "URL" => "uri",
            _ => null
        };

        return field.PrimaryType switch
        {
            "Number" => "number",
            "Boolean" => "boolean",
            "Array" => "array",
            "Object" => "object",
            _ => "string"
        };
    }
}
