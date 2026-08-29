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
        var propsObj = new JsonObject();
        foreach (var propKvp in node.Properties)
        {
            string childName = propKvp.Key;
            var childNode = propKvp.Value;
            var propDef = new JsonObject();

            if (childNode.Kind == "Object")
            {
                if (childNode.IsNullable)
                    propDef["type"] = new JsonArray { "object", "null" };
                else
                    propDef["type"] = "object";

                propDef["properties"] = BuildJsonSchemaProperties(childNode);
                propDef["required"] = BuildJsonSchemaRequired(childNode);
            }
            else if (childNode.Kind == "Array")
            {
                if (childNode.IsNullable)
                    propDef["type"] = new JsonArray { "array", "null" };
                else
                    propDef["type"] = "array";

                var itemDef = new JsonObject();
                if (childNode.ElementNode?.Kind == "Object")
                {
                    itemDef["type"] = "object";
                    itemDef["properties"] = BuildJsonSchemaProperties(childNode.ElementNode);
                    itemDef["required"] = BuildJsonSchemaRequired(childNode.ElementNode);
                }
                else
                {
                    itemDef["type"] = childNode.ElementNode?.JsonSchemaBaseType ?? "string";
                    if (childNode.ElementNode?.Format != null)
                        itemDef["format"] = childNode.ElementNode.Format;
                }
                propDef["items"] = itemDef;
            }
            else
            {
                if (childNode.IsNullable)
                    propDef["type"] = new JsonArray { childNode.JsonSchemaBaseType, "null" };
                else
                    propDef["type"] = childNode.JsonSchemaBaseType;

                if (childNode.Format != null)
                    propDef["format"] = childNode.Format;
            }

            propsObj[childName] = propDef;
        }
        return propsObj;
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
