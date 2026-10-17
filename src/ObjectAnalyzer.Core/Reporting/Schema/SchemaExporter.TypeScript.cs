using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;

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

        GenerateTypeScriptInterfaces(rootTree, interfaceName, sb, new HashSet<string>(StringComparer.Ordinal));

        return sb.ToString();
    }

    // ─────────────────────────────────────────────
    //  TypeScript Generator
    // ─────────────────────────────────────────────

    private static string GenerateTypeScriptInterfaces(SchemaNode node, string interfaceName, StringBuilder sb, HashSet<string> names)
    {
        interfaceName = UniqueIdentifier(interfaceName, names);
        var members = new List<string>();
        foreach (var pair in node.Properties)
        {
            string type = TypeScriptNodeType(pair.Value, SanitizeIdentifier(pair.Key), sb, names);
            string optional = pair.Value.IsNullable ? "?" : "";
            members.Add($"  {FormatTsPropertyName(pair.Key)}{optional}: {type};");
        }
        sb.AppendLine(CultureInfo.InvariantCulture, $"export interface {interfaceName} {{");
        foreach (string member in members) sb.AppendLine(member);
        sb.AppendLine("}");
        sb.AppendLine();
        return interfaceName;
    }

    private static string TypeScriptNodeType(SchemaNode node, string hint, StringBuilder sb, HashSet<string> names) => node.Kind switch
    {
        "Object" => GenerateTypeScriptInterfaces(node, hint + "Dto", sb, names),
        "Array" => (node.ElementNode is null ? "string" : TypeScriptNodeType(node.ElementNode, hint + "Item", sb, names)) + "[]",
        _ => node.TsBaseType
    };

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
        return isValid ? name : System.Text.Json.JsonSerializer.Serialize(name);
    }
}
