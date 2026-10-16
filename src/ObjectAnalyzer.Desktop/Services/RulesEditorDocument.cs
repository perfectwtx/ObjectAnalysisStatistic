using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ObjectAnalyzer.Analysis;
using ObjectAnalyzer.Analysis.Rules;

namespace ObjectAnalyzer.Desktop.Services;

/// <summary>UI-independent rules editing model. Each row retains its original advanced properties.</summary>
public sealed class RulesEditorDocument
{
    public const string Template = """
    {
      // Runtime：控制数据展开与解析行为
      "runtime": {
        "flatten": true
      },

      "fields": {
        "gender": { "enumValues": ["Male", "Female", "Unknown"] },
        "age": { "required": true, "minValue": 0, "maxValue": 120 },
        "email": { "pattern": "\\S+@\\S+", "required": true },
        "id": { "unique": true },

        // 缺失值与显式 null 都会计入空值率
        "address.district": { "nullRateMax": 0.2 },

        // 将字符串先按 JSON 解析，再展开子字段
        "line": { "transform": { "parse": "json" } },
        "line.body": { "transform": { "parse": "json" } }
      },

      // Excel/CSV：指定列按 JSON 解析后再统计
      "valueParsers": [
        { "source": "payload", "parseType": "auto", "flatten": true, "header": 1 }
      ],

      "expectations": { "minRowCount": 3, "maxDuplicateRate": 0 }
    }
    """;

    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };
    private readonly JsonObject _root;

    private RulesEditorDocument(JsonObject root) => _root = root;

    public ObservableCollection<RulesEditorField> Fields { get; } = new();
    public bool Flatten { get; set; } = true;
    public string MinRowCount { get; set; } = string.Empty;
    public string MaxDuplicateRate { get; set; } = string.Empty;

    public static RulesEditorDocument Parse(string text)
    {
        var root = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        }) as JsonObject ?? throw new InvalidOperationException("规则根节点必须是 JSON 对象。");

        // Validate types before touching the current form, including advanced properties.
        _ = JsonSerializer.Deserialize<AnalysisRules>(text, AnalysisRules.RulesJsonOptions);
        if (Key(root, "fields") is { } fieldsKey && root[fieldsKey] is not JsonObject)
            throw new InvalidOperationException("fields 必须是 JSON 对象。");
        var document = new RulesEditorDocument(root);
        document.Flatten = Read(Section(root, "runtime"), "flatten")?.GetValue<bool>() ?? true;
        foreach (var item in Section(root, "fields"))
        {
            if (item.Value is not JsonObject rule)
                throw new InvalidOperationException($"字段“{item.Key}”的规则必须是 JSON 对象。");
            document.Fields.Add(new RulesEditorField
            {
                Name = item.Key,
                OriginalRule = (JsonObject)rule.DeepClone(),
                Required = Read(rule, "required")?.GetValue<bool>() ?? false,
                Unique = Read(rule, "unique")?.GetValue<bool>() ?? false,
                MinValue = Read(rule, "minValue")?.ToJsonString(),
                MaxValue = Read(rule, "maxValue")?.ToJsonString(),
                NullRateMax = Read(rule, "nullRateMax")?.ToJsonString(),
                Pattern = Read(rule, "pattern")?.GetValue<string>() ?? string.Empty
            });
        }
        var expectations = Section(root, "expectations");
        document.MinRowCount = Read(expectations, "minRowCount")?.ToJsonString() ?? string.Empty;
        document.MaxDuplicateRate = Read(expectations, "maxDuplicateRate")?.ToJsonString() ?? string.Empty;
        return document;
    }

    // Fingerprints cover editable values, rather than the JSON formatting or unmapped properties.
    public string FormFingerprint() => JsonSerializer.Serialize(new
    {
        Flatten, MinRowCount, MaxDuplicateRate,
        Fields = Fields.Select(row => new
        {
            row.Name, row.Required, row.Unique, row.MinValue, row.MaxValue, row.NullRateMax, row.Pattern
        })
    });

    public string ToJson()
    {
        var root = (JsonObject)_root.DeepClone();
        var runtime = (JsonObject)Section(root, "runtime").DeepClone();
        Write(runtime, "flatten", JsonValue.Create(Flatten));
        Write(root, "runtime", runtime);

        var fields = new JsonObject();
        foreach (var row in Fields)
        {
            if (string.IsNullOrWhiteSpace(row.Name))
                throw new InvalidOperationException("字段路径不能为空。");
            if (fields.ContainsKey(row.Name))
                throw new InvalidOperationException($"字段路径“{row.Name}”重复，请为每个字段使用唯一的路径。");

            // Clone by row identity, so renames (including swaps) retain enum/transform/PII rules.
            var rule = (JsonObject)row.OriginalRule.DeepClone();
            Write(rule, "required", JsonValue.Create(row.Required));
            Write(rule, "unique", JsonValue.Create(row.Unique));
            WriteNumber(rule, "minValue", row.MinValue);
            WriteNumber(rule, "maxValue", row.MaxValue);
            WriteNumber(rule, "nullRateMax", row.NullRateMax);
            Write(rule, "pattern", string.IsNullOrEmpty(row.Pattern) ? null : JsonValue.Create(row.Pattern));
            fields.Add(row.Name, rule);
        }
        Write(root, "fields", fields);

        var expectations = (JsonObject)Section(root, "expectations").DeepClone();
        if (string.IsNullOrWhiteSpace(MinRowCount))
            Write(expectations, "minRowCount", null);
        else
        {
            if (!long.TryParse(MinRowCount, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) || count < 0)
                throw new InvalidOperationException("minRowCount 必须是大于或等于 0 的 64 位整数。");
            Write(expectations, "minRowCount", JsonValue.Create(count));
        }
        WriteNumber(expectations, "maxDuplicateRate", MaxDuplicateRate);
        Write(root, "expectations", expectations);
        // Semantic errors remain editable in JSONC; only validation/save blocks them.
        return root.ToJsonString(s_jsonOptions);
    }

    public static AnalysisRules Validate(string text)
    {
        _ = Parse(text);
        var unknowns = RulesLint.Scan(text);
        if (unknowns.Count > 0)
            throw new InvalidOperationException(RulesLint.Describe(unknowns));
        var rules = JsonSerializer.Deserialize<AnalysisRules>(text, AnalysisRules.RulesJsonOptions)
            ?? throw new InvalidOperationException("规则内容不能为空。");
        foreach (var (name, rule) in rules.Fields)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidOperationException("字段路径不能为空。");
            CheckFinite(rule.MinValue, name + ".minValue");
            CheckFinite(rule.MaxValue, name + ".maxValue");
            if (rule.MinValue > rule.MaxValue)
                throw new InvalidOperationException($"字段“{name}”的最小值不能大于最大值。");
            CheckRatio(rule.NullRateMax, name + ".nullRateMax");
            CheckRatio(rule.MaxOutlierRatio, name + ".maxOutlierRatio");
            if (!string.IsNullOrWhiteSpace(rule.Pattern))
            {
                try { _ = new Regex(rule.Pattern, RegexOptions.None, TimeSpan.FromSeconds(1)); }
                catch (ArgumentException ex)
                {
                    throw new InvalidOperationException($"字段“{name}”的正则表达式无效：{ex.Message}", ex);
                }
            }
        }
        if (rules.Expectations?.MinRowCount < 0)
            throw new InvalidOperationException("minRowCount 不能为负数。");
        CheckRatio(rules.Expectations?.MaxDuplicateRate, "maxDuplicateRate");
        return rules;
    }

    private static void CheckFinite(double? value, string name)
    {
        if (value.HasValue && !double.IsFinite(value.Value))
            throw new InvalidOperationException($"{name} 必须是有限数字。");
    }

    private static void CheckRatio(double? value, string name)
    {
        CheckFinite(value, name);
        if (value < 0 || value > 1)
            throw new InvalidOperationException($"{name} 必须在 0 到 1 之间。");
    }

    private static void WriteNumber(JsonObject target, string key, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            Write(target, key, null);
            return;
        }
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
            throw new InvalidOperationException($"{key} 必须是有效数字。");
        Write(target, key, JsonValue.Create(value));
    }

    private static string? Key(JsonObject target, string key)
    {
        var matches = target.Select(pair => pair.Key)
            .Where(name => string.Equals(name, key, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length > 1)
            throw new InvalidOperationException($"配置属性“{key}”重复。");
        return matches.FirstOrDefault();
    }

    private static JsonNode? Read(JsonObject target, string key) => Key(target, key) is { } name ? target[name] : null;

    private static JsonObject Section(JsonObject target, string key)
    {
        var node = Read(target, key);
        return node is null ? new JsonObject() : node as JsonObject
            ?? throw new InvalidOperationException($"{key} 必须是 JSON 对象。");
    }

    private static void Write(JsonObject target, string key, JsonNode? value)
    {
        if (Key(target, key) is { } existing)
            target.Remove(existing);
        if (value is not null)
            target[key] = value;
    }
}

public sealed class RulesEditorField
{
    internal JsonObject OriginalRule { get; init; } = new();
    public string Name { get; set; } = string.Empty;
    public bool Required { get; set; }
    public bool Unique { get; set; }
    public string? MinValue { get; set; }
    public string? MaxValue { get; set; }
    public string? NullRateMax { get; set; }
    public string Pattern { get; set; } = string.Empty;
}
