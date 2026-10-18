using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using ObjectAnalyzer.Data;

namespace ObjectAnalyzer.Reporting;

public enum SchemaNamingTarget { CSharp, TypeScript, OpenApi, Protobuf, Sql }

/// <summary>Deterministic target-specific identifiers; wire names never enter the inferred type graph.</summary>
public sealed class SchemaNamePlan
{
    private readonly Dictionary<SchemaType, string> _types = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<SchemaType, IReadOnlyDictionary<string, string>> _properties = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<SchemaType, string> _paths = new(ReferenceEqualityComparer.Instance);
    public string SourcePath(SchemaType type) => _paths[type];
    private readonly List<SchemaType> _objects = new();
    public IReadOnlyList<SchemaType> Objects => _objects.AsReadOnly();
    public string TypeName(SchemaType type) => _types[type];
    public string PropertyName(SchemaType owner, string wireName) => _properties[owner][wireName];
    public static SchemaNamePlan Create(SchemaDocument document, SchemaNamingTarget target, string rootName = "GeneratedSchema")
    {
        var result = new SchemaNamePlan(); var usedTypes = new HashSet<string>(StringComparer.Ordinal);
        result.Visit(document.Root, rootName, target, usedTypes, 0, "");
        return result;
    }
    private void Visit(SchemaType type, string hint, SchemaNamingTarget target, HashSet<string> usedTypes, int depth, string path)
    {
        if (depth > 128) throw new AnalysisResourceLimitException("schema-export-depth", 128);
        if (_paths.TryAdd(type, path) && _paths.Count > 20_000) throw new AnalysisResourceLimitException("schema-export-nodes", 20_000);
        if (type.Kinds.Contains(RecordValueKind.Object))
        {
            if (_types.ContainsKey(type)) return;
            string name = Allocate(Identifier(hint, true), usedTypes); _types.Add(type, name); _objects.Add(type);
            var used = new HashSet<string>(target == SchemaNamingTarget.Sql ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            if (target == SchemaNamingTarget.CSharp) used.UnionWith([name, "EqualityContract", "Clone", "PrintMembers", "Equals", "GetHashCode", "ToString", "AdditionalProperties"]);
            if (target == SchemaNamingTarget.Protobuf) used.UnionWith(["syntax", "message", "enum", "service", "rpc", "returns", "package", "import", "public", "weak", "option", "optional", "required", "repeated", "oneof", "map", "reserved", "extensions", "extend", "group", "stream", "to", "max"]);
            var properties = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in type.Properties.OrderBy(item => item.Name, StringComparer.Ordinal))
                properties.Add(property.Name, Allocate(Identifier(property.Name, target == SchemaNamingTarget.CSharp), used));
            _properties.Add(type, properties.ToFrozenDictionary(StringComparer.Ordinal));
            foreach (var property in type.Properties.OrderBy(item => item.Name, StringComparer.Ordinal))
                Visit(property.Type, Identifier(property.Name, true) + "Dto", target, usedTypes, depth + 1, SchemaRenderContext.Child(path, property.Name));
        }
        if (type.Element is not null)
        {
            string elementHint = hint.EndsWith("Dto", StringComparison.Ordinal) ? hint[..^3] + "ItemDto" : hint + "Item";
            Visit(type.Element, elementHint, target, usedTypes, depth + 1, path + "[]");
        }
    }
    internal static string Identifier(string name, bool pascal)
    {
        var text = new StringBuilder();
        foreach (char ch in name)
            if (char.IsAsciiLetterOrDigit(ch) || ch == '_') text.Append(ch);
            else if (char.IsLetterOrDigit(ch)) text.Append("_u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
            else text.Append('_');
        if (text.Length == 0) text.Append("Property");
        if (char.IsDigit(text[0])) text.Insert(0, '_');
        if (pascal) text[0] = char.ToUpperInvariant(text[0]);
        return text.ToString();
    }
    internal static string Allocate(string candidate, HashSet<string> used)
    {
        string result = candidate;
        for (int suffix = 2; !used.Add(result); suffix++) result = candidate + suffix.ToString(CultureInfo.InvariantCulture);
        return result;
    }
}
