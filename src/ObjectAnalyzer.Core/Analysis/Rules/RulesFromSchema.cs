using System.Text.Json;

namespace ObjectAnalyzer.Analysis;

/// <summary>
/// JSON Schema（draft-07 / 2020-12 常用子集）→ AnalysisRules 转换器。
/// 映射：type → primaryType（format: date-time/date/time → DateTime）、enum → enumValues、
/// required → required、minimum/maximum → minValue/maxValue、pattern → pattern、default → defaultValue。
/// 嵌套对象按扁平化路径（a.b.c）展开；数组字段无法静态确定具体索引，跳过并记录；
/// $ref 仅解析本地指针（#/$defs/...、#/definitions/...），外部引用跳过。
/// </summary>
public static class RulesFromSchema
{
    public static AnalysisRules Convert(string schemaJson, List<string>? skipped = null)
    {
        using var doc = JsonDocument.Parse(schemaJson);
        return Convert(doc.RootElement, skipped);
    }

    public static AnalysisRules Convert(JsonElement root, List<string>? skipped = null)
    {
        skipped ??= new List<string>();
        var rules = new AnalysisRules();
        ConvertObject(root, "", rules.Fields, skipped, root, new HashSet<string>(StringComparer.Ordinal));
        return rules;
    }

    private static void ConvertObject(
        JsonElement schema,
        string prefix,
        Dictionary<string, FieldRule> fields,
        List<string> skipped,
        JsonElement root,
        HashSet<string> visiting)
    {
        schema = ResolveRef(schema, root, visiting);
        if (schema.ValueKind != JsonValueKind.Object) return;

        var required = new HashSet<string>(StringComparer.Ordinal);
        if (schema.TryGetProperty("required", out var reqEl) && reqEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in reqEl.EnumerateArray())
            {
                if (r.ValueKind == JsonValueKind.String)
                    required.Add(r.GetString()!);
            }
        }

        if (!schema.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Object)
            return;

        foreach (var prop in props.EnumerateObject())
        {
            string path = FieldPath.Parse(prefix).AppendProperty(prop.Name).ToString();
            // 每个属性独立副本：兄弟属性重复引用同一个 $defs 是合法的（菱形引用），不能共享环路守卫
            ConvertProperty(prop.Value, path, required.Contains(prop.Name), fields, skipped, root, new HashSet<string>(visiting));
        }
    }

    private static void ConvertProperty(
        JsonElement schemaEl,
        string path,
        bool isRequired,
        Dictionary<string, FieldRule> fields,
        List<string> skipped,
        JsonElement root,
        HashSet<string> visiting)
    {
        var schema = ResolveRef(schemaEl, root, visiting);
        if (schema.ValueKind != JsonValueKind.Object)
        {
            skipped.Add(path);
            return;
        }

        var rule = new FieldRule();

        // type：字符串或 ["string","null"] 数组形态
        string? type = null;
        if (schema.TryGetProperty("type", out var typeEl))
        {
            if (typeEl.ValueKind == JsonValueKind.String)
            {
                type = typeEl.GetString();
            }
            else if (typeEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in typeEl.EnumerateArray())
                {
                    if (t.ValueKind != JsonValueKind.String) continue;
                    if (t.GetString() == "null") continue;
                    type = t.GetString();
                    break;
                }
            }
        }

        // 枚举：推导 enumValues；未声明 type 时按成员类型反推 primaryType
        if (schema.TryGetProperty("enum", out var enumEl) && enumEl.ValueKind == JsonValueKind.Array)
        {
            var values = new List<JsonElement>();
            bool allStrings = true, allNumbers = true;
            foreach (var v in enumEl.EnumerateArray())
            {
                values.Add(v.Clone());
                if (v.ValueKind != JsonValueKind.String) allStrings = false;
                if (v.ValueKind != JsonValueKind.Number) allNumbers = false;
            }
            if (values.Count > 0)
            {
                rule.EnumValues = values;
                if (type is null)
                    rule.PrimaryType = allStrings ? "String" : allNumbers ? "Number" : null;
            }
        }

        switch (type)
        {
            case "object":
                // 对象本身不产生规则，递归展开其属性
                ConvertObject(schema, path, fields, skipped, root, new HashSet<string>(visiting));
                return;
            case "array":
                skipped.Add($"{path}（数组字段无法静态确定具体索引）");
                return;
            case "string":
                rule.PrimaryType ??= IsTemporalFormat(schema) ? "DateTime" : "String";
                break;
            case "integer":
            case "number":
                rule.PrimaryType ??= "Number";
                break;
            case "boolean":
                rule.PrimaryType ??= "Boolean";
                break;
            default:
                if (rule.EnumValues is null)
                {
                    skipped.Add(path);
                    return;
                }
                break;
        }

        if (schema.TryGetProperty("minimum", out var minEl) && minEl.ValueKind == JsonValueKind.Number)
            rule.MinValue = minEl.GetDouble();
        if (schema.TryGetProperty("maximum", out var maxEl) && maxEl.ValueKind == JsonValueKind.Number)
            rule.MaxValue = maxEl.GetDouble();
        if (schema.TryGetProperty("pattern", out var patEl) && patEl.ValueKind == JsonValueKind.String)
            rule.Pattern = patEl.GetString();
        if (schema.TryGetProperty("default", out var defEl)
            && defEl.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            rule.DefaultValue = defEl.Clone();
        }
        if (isRequired)
            rule.Required = true;

        fields[path] = rule;
    }

    private static bool IsTemporalFormat(JsonElement schema)
    {
        if (schema.TryGetProperty("format", out var fmt) && fmt.ValueKind == JsonValueKind.String)
        {
            return fmt.GetString() is "date-time" or "date" or "time";
        }
        return false;
    }

    /// <summary>
    /// 解析本地 $ref（#/$defs/Name、#/definitions/Name、#/properties/...）。
    /// 链式解析（ref 指向的目标仍含 ref 时继续追踪）；环路保护：同一指针在链上重复出现，
    /// 或链长超过 32 跳时抛出 InvalidOperationException。
    /// </summary>
    private static JsonElement ResolveRef(JsonElement schema, JsonElement root, HashSet<string> visiting)
    {
        for (var hop = 0; hop < 32; hop++)
        {
            if (schema.ValueKind != JsonValueKind.Object)
                return schema;
            if (!schema.TryGetProperty("$ref", out var refEl) || refEl.ValueKind != JsonValueKind.String)
                return schema;

            var pointer = refEl.GetString();
            if (pointer is null || !pointer.StartsWith("#/", StringComparison.Ordinal))
                return schema; // 外部引用无法解析，交由调用方按"无类型"处理

            if (!visiting.Add(pointer))
            {
                throw new InvalidOperationException($"JSON Schema $ref 存在循环引用: {pointer}");
            }

            var current = root;
            bool reachable = true;
            foreach (var seg in pointer[2..].Split('/'))
            {
                var key = seg.Replace("~1", "/").Replace("~0", "~");
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(key, out current))
                {
                    reachable = false;
                    break;
                }
            }
            if (!reachable)
                return schema; // 指针不可达，交由调用方处理

            schema = current;
        }

        throw new InvalidOperationException("JSON Schema $ref 链过长（>32 跳），疑似循环引用。");
    }
}
