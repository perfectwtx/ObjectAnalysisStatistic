using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace ObjectAnalyzer.Analysis.Rules;

/// <summary>
/// 规则文本里「写了但后端不认识」的键。
/// 反序列化时这些键会被静默忽略（STJ 默认行为），用户拼错规则名时得不到任何反馈。
/// </summary>
/// <param name="Path">所在路径，顶层为 <c>$</c>，字段级为 <c>fields.age</c> 这类。</param>
/// <param name="Name">未识别的键名。</param>
/// <param name="Suggestion">按编辑距离猜的最接近的合法键名；猜不出时为 null。</param>
public sealed record UnknownRuleKey(string Path, string Name, string? Suggestion);

/// <summary>
/// 规则文本静态检查：找出未知（拼写错误 / 已废弃）的规则键名。
///
/// 只做「读」的旁检查，不改动 <see cref="AnalysisRules"/> 的反序列化行为
/// （没有开 <c>JsonUnmappedMemberHandling.Disallow</c>），
/// 因此含历史/废弃键的旧规则文件仍然能读进来 —— 只是调用方可以据此报错而不是装作没看见。
/// API 与 CLI 都在反序列化前跑一次 Scan，把拼错的键拦下来。
/// </summary>
public static class RulesLint
{
    /// <summary>规则模型的嵌套结构：哪些属性下面还有一层规则对象。</summary>
    private static readonly HashSet<Type> RuleTypes =
    [
        typeof(AnalysisRules), typeof(FieldRule), typeof(FieldTransform),
        typeof(PiiRule), typeof(PiiMaskConfig), typeof(AnalysisExpectations),
        typeof(ValueParserConfig)
    ];

    /// <summary>
    /// <c>runtime</c> 节点在重构后是 <see cref="System.Text.Json.Nodes.JsonObject"/>，
    /// 不再通过反射枚举子键。保留一份白名单用于静态检查拼写错误 / 已废弃键。
    /// </summary>
    private static readonly string[] RuntimeTopLevelKeys =
    [
        "flatten", "csvInferNumbers", "maxValuesToShow", "maxParallelism", "allowUnknownRules",
        "selectedFields", "filter",
        "sampling", "jsonPath", "schema", "distribution", "pii", "gates", "p4",
        "checkpoint", "memory", "progress", "audit", "outputs"
    ];

    private static readonly string[] SamplingKeys = ["reservoir", "step"];
    private static readonly string[] JsonPathKeys = ["root", "record"];
    private static readonly string[] SchemaKeys = ["snapshot", "diffBaseline", "diffReport", "allowBreakingChanges"];
    private static readonly string[] DistributionKeys = ["snapshot", "diffBaseline", "diffReport", "lowConfidenceWidth"];
    private static readonly string[] PiiKeys = ["off", "report", "failOnLevel"];
    private static readonly string[] GatesKeys = ["failOnViolations", "failOnOutlierRatio"];
    private static readonly string[] P4Keys = ["correlation", "stringPatterns", "unicodeHygiene", "timeSeries", "distributionSnapshot", "fuzzyDedupField"];
    private static readonly string[] CheckpointKeys = ["path", "resumeFrom", "baseline"];
    private static readonly string[] MemoryKeys = ["maxMB", "mode"];
    private static readonly string[] ProgressKeys = ["enabled"];
    private static readonly string[] AuditKeys = ["log", "operator"];
    private static readonly string[] OutputsKeys = ["primary", "violations", "quality", "schemaCode"];

    private static readonly Dictionary<string, string[]> RuntimeChildKeys = new()
    {
        ["sampling"] = SamplingKeys,
        ["jsonPath"] = JsonPathKeys,
        ["schema"] = SchemaKeys,
        ["distribution"] = DistributionKeys,
        ["pii"] = PiiKeys,
        ["gates"] = GatesKeys,
        ["p4"] = P4Keys,
        ["checkpoint"] = CheckpointKeys,
        ["memory"] = MemoryKeys,
        ["progress"] = ProgressKeys,
        ["audit"] = AuditKeys,
        ["outputs"] = OutputsKeys,
    };

    private static readonly Dictionary<Type, string[]> KnownNamesCache = [];

    /// <summary>
    /// 把未知键列表写成一句中文说明，用于报错文案。
    /// 只点名前 3 项，其余报数量——规则里拼错的键通常成片出现，全列出来反而看不清。
    /// </summary>
    public static string Describe(List<UnknownRuleKey> unknowns)
    {
        var shown = unknowns.Take(3).Select(u =>
            $"{(u.Path == "$" ? "顶层" : u.Path)} 的 \"{u.Name}\"" +
            (u.Suggestion is null ? "" : $"（是否想写 \"{u.Suggestion}\"？）"));
        var more = unknowns.Count > 3 ? $"，共 {unknowns.Count} 项" : "";

        return $"规则里有后端不认识的键：{string.Join("；", shown)}{more}。" +
               "这些键在分析时会被忽略（多半是拼错或已废弃）。" +
               "确认无需处理可加 --allow-unknown-rules（CLI）/ allowUnknownRules=true（分析接口）/ strict=false（校验接口）跳过。";
    }

    /// <summary>
    /// 扫描规则文本中的未知键。语法本身不合法时返回空列表 ——
    /// 语法错误由主反序列化流程负责报错，避免同一处问题报两遍。
    /// </summary>
    public static List<UnknownRuleKey> Scan(string json)
    {
        var found = new List<UnknownRuleKey>();
        if (string.IsNullOrWhiteSpace(json)) return found;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
        }
        catch (JsonException)
        {
            return found;
        }

        using (doc)
        {
            Walk(doc.RootElement, typeof(AnalysisRules), "$", found);
        }
        return found;
    }

    private static void Walk(JsonElement el, Type type, string path, List<UnknownRuleKey> found)
    {
        if (el.ValueKind != JsonValueKind.Object) return;

        var known = KnownNames(type);
        foreach (var prop in el.EnumerateObject())
        {
            var matched = known.FirstOrDefault(k => string.Equals(k, prop.Name, StringComparison.OrdinalIgnoreCase));
            if (matched is null)
            {
                found.Add(new UnknownRuleKey(path, prop.Name, Suggest(prop.Name, known)));
                continue;
            }

            // runtime 节点：JsonObject 不再走反射，而是按白名单检查
            if (type == typeof(AnalysisRules) && string.Equals(matched, "runtime", StringComparison.OrdinalIgnoreCase))
            {
                WalkRuntime(prop.Value, path, found);
                continue;
            }

            var child = ChildType(type, matched, out var isDictionary);
            if (child is null) continue;

            if (isDictionary)
            {
                // fields 这类 map：键是数据字段名（任意），值才是规则对象，
                // 所以路径要带上 map 自身那一段（fields.age），不能只留 age
                if (prop.Value.ValueKind != JsonValueKind.Object) continue;
                var mapPath = Join(path, prop.Name);
                foreach (var entry in prop.Value.EnumerateObject())
                {
                    Walk(entry.Value, child, Join(mapPath, entry.Name), found);
                }
            }
            else if (prop.Value.ValueKind == JsonValueKind.Object)
            {
                Walk(prop.Value, child, Join(path, prop.Name), found);
            }
            else if (prop.Value.ValueKind == JsonValueKind.Array)
            {
                // valueParsers 这类数组：元素都是同一种规则对象
                var i = 0;
                foreach (var item in prop.Value.EnumerateArray())
                {
                    Walk(item, child, $"{Join(path, prop.Name)}[{i++}]", found);
                }
            }
        }
    }

    private static void WalkRuntime(JsonElement el, string path, List<UnknownRuleKey> found)
    {
        if (el.ValueKind != JsonValueKind.Object) return;
        var runtimePath = Join(path, "runtime");
        foreach (var prop in el.EnumerateObject())
        {
            if (!RuntimeTopLevelKeys.Any(k => string.Equals(k, prop.Name, StringComparison.OrdinalIgnoreCase)))
            {
                found.Add(new UnknownRuleKey(runtimePath, prop.Name, Suggest(prop.Name, RuntimeTopLevelKeys)));
                continue;
            }

            // 子节点（sampling / jsonPath / ...）：白名单校验
            if (RuntimeChildKeys.TryGetValue(prop.Name, out var childKnown))
            {
                if (prop.Value.ValueKind != JsonValueKind.Object) continue;
                var childPath = Join(runtimePath, prop.Name);
                foreach (var sub in prop.Value.EnumerateObject())
                {
                    if (!childKnown.Any(k => string.Equals(k, sub.Name, StringComparison.OrdinalIgnoreCase)))
                    {
                        found.Add(new UnknownRuleKey(childPath, sub.Name, Suggest(sub.Name, childKnown)));
                    }
                }
            }
        }
    }

    /// <summary>取属性的 JSON 名集合（大小写不敏感匹配，与 RulesJsonOptions 一致）。</summary>
    private static string[] KnownNames(Type type)
    {
        if (KnownNamesCache.TryGetValue(type, out var cached)) return cached;

        var names = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0)
            .Where(p => p.GetCustomAttribute<JsonIgnoreAttribute>() is null)
            .Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
                         ?? JsonNamingPolicy.CamelCase.ConvertName(p.Name))
            .ToArray();

        KnownNamesCache[type] = names;
        return names;
    }

    /// <summary>属性的下一层规则类型（没有则 null）。</summary>
    private static Type? ChildType(Type owner, string jsonName, out bool isDictionary)
    {
        isDictionary = false;
        var prop = owner.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(p => string.Equals(
                p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
                ?? JsonNamingPolicy.CamelCase.ConvertName(p.Name),
                jsonName, StringComparison.OrdinalIgnoreCase));
        if (prop is null) return null;

        var t = prop.PropertyType;
        var underlying = Nullable.GetUnderlyingType(t) ?? t;

        // Dictionary<string, T>：值是规则对象，键是数据字段名
        if (underlying.IsGenericType && underlying.GetGenericTypeDefinition() == typeof(Dictionary<,>))
        {
            isDictionary = true;
            var value = underlying.GetGenericArguments()[1];
            return RuleTypes.Contains(value) ? value : null;
        }

        // List<T> / T[]：元素是规则对象
        if (underlying.IsGenericType && underlying.GetGenericTypeDefinition() == typeof(List<>))
        {
            var item = underlying.GetGenericArguments()[0];
            return RuleTypes.Contains(item) ? item : null;
        }
        if (underlying.IsArray)
        {
            var item = underlying.GetElementType();
            return item is not null && RuleTypes.Contains(item) ? item : null;
        }

        return RuleTypes.Contains(underlying) ? underlying : null;
    }

    private static string Join(string path, string name)
        => path == "$" ? name : $"{path}.{name}";

    /// <summary>按编辑距离猜一个最接近的合法键名；差太远就返回 null。</summary>
    private static string? Suggest(string name, string[] known)
    {
        // 忽略大小写与 min_value / min-value 这类分隔符差异
        var norm = name.Replace("_", "").Replace("-", "").ToLowerInvariant();
        if (norm.Length == 0) return null;

        string? best = null;
        var bestDistance = int.MaxValue;
        foreach (var k in known)
        {
            var kn = k.ToLowerInvariant();
            var d = Levenshtein(norm, kn);
            if (d == 0) return k;
            if (d < bestDistance)
            {
                bestDistance = d;
                best = k;
            }
        }

        // 越长的键容忍度略高，但最多 3 个字符的差异
        return bestDistance <= Math.Min(3, Math.Max(1, norm.Length / 3)) ? best : null;
    }

    private static int Levenshtein(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
