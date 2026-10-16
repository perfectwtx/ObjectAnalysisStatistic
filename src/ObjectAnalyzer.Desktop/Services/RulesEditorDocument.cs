using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Reflection;
using System.Text.Json.Serialization;
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
    public List<RulesEditorOption> RuntimeOptions { get; private set; } = [];
    public ObservableCollection<RulesEditorParser> ValueParsers { get; } = new();
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
        document.RuntimeOptions = RulesEditorOption.Runtime(Section(root, "runtime"));
        if (Read(root, "valueParsers") is JsonArray parsers)
            foreach (var parser in parsers)
                document.ValueParsers.Add(new RulesEditorParser(parser as JsonObject
                    ?? throw new InvalidOperationException("值解析器必须是对象。")));
        foreach (var item in Section(root, "fields"))
        {
            if (item.Value is not JsonObject rule)
                throw new InvalidOperationException($"字段“{item.Key}”的规则必须是 JSON 对象。");
            document.Fields.Add(new RulesEditorField
            {
                Name = item.Key,
                OriginalRule = (JsonObject)rule.DeepClone(),
                Options = RulesEditorOption.ForType(typeof(FieldRule), rule,
                    ["required", "unique", "minValue", "maxValue", "nullRateMax", "pattern"]),
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
        Runtime = RuntimeOptions.Select(o => o.Text),
        Parsers = ValueParsers.Select(p => p.Options.Select(o => o.Text)),
        Fields = Fields.Select(row => new
        {
            row.Name, row.Required, row.Unique, row.MinValue, row.MaxValue, row.NullRateMax, row.Pattern,
            Options = row.Options.Select(o => o.Text)
        })
    });

    public string ToJson()
    {
        var root = (JsonObject)_root.DeepClone();
        var runtime = (JsonObject)Section(root, "runtime").DeepClone();
        RulesEditorOption.Apply(runtime, RuntimeOptions);
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
            RulesEditorOption.Apply(rule, row.Options);
            Write(rule, "required", JsonValue.Create(row.Required));
            Write(rule, "unique", JsonValue.Create(row.Unique));
            WriteNumber(rule, "minValue", row.MinValue);
            WriteNumber(rule, "maxValue", row.MaxValue);
            WriteNumber(rule, "nullRateMax", row.NullRateMax);
            Write(rule, "pattern", string.IsNullOrEmpty(row.Pattern) ? null : JsonValue.Create(row.Pattern));
            fields.Add(row.Name, rule);
        }
        Write(root, "fields", fields);
        if (ValueParsers.Count > 0 || Read(root, "valueParsers") is not null)
            Write(root, "valueParsers", new JsonArray(ValueParsers.Select(p => (JsonNode)p.ToJson()).ToArray()));

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
        var document = Parse(text);
        RulesEditorOption.Apply(new JsonObject(), document.RuntimeOptions);
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
        foreach (var parser in rules.ValueParsers ?? [])
        {
            if (string.IsNullOrWhiteSpace(parser.Source))
                throw new InvalidOperationException("值解析器 source 不能为空。");
            if (parser.Header < 0)
                throw new InvalidOperationException("值解析器 header 不能为负数。");
            if (parser.ParseType is not ("none" or "auto" or "json" or "json-string"))
                throw new InvalidOperationException("值解析器 parseType 必须为 none / auto / json / json-string。");
        }
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
    public IEnumerable<RulesEditorOptionGroup> OptionGroups => Options.GroupBy(o =>
        o.Path.StartsWith("transform.", StringComparison.Ordinal) ? "值转换" :
        o.Path.StartsWith("pii.", StringComparison.Ordinal) || o.Path == "redact" ? "敏感信息与脱敏" : "类型与取值")
        .Select(g => new RulesEditorOptionGroup(g.Key, g.ToArray()));
    public List<RulesEditorOption> Options { get; init; } = RulesEditorOption.ForType(typeof(FieldRule), new(),
        ["required", "unique", "minValue", "maxValue", "nullRateMax", "pattern"]);
    internal JsonObject OriginalRule { get; init; } = new();
    public string Name { get; set; } = string.Empty;
    public bool Required { get; set; }
    public bool Unique { get; set; }
    public string? MinValue { get; set; }
    public string? MaxValue { get; set; }
    public string? NullRateMax { get; set; }
    public string Pattern { get; set; } = string.Empty;
}

public sealed record RulesEditorChoice(string Value, string Label)
{
    public override string ToString() => Label;
}
public sealed record RulesEditorOptionGroup(string Name, IReadOnlyList<RulesEditorOption> Options);

public sealed class RulesEditorParser
{
    private readonly JsonObject _original;
    public RulesEditorParser() : this(new JsonObject()) { }
    public RulesEditorParser(JsonObject original)
    {
        _original = (JsonObject)original.DeepClone();
        Options = RulesEditorOption.ForType(typeof(ValueParserConfig), original);
    }
    public List<RulesEditorOption> Options { get; }
    public JsonObject ToJson()
    {
        var result = (JsonObject)_original.DeepClone();
        RulesEditorOption.Apply(result, Options);
        return result;
    }
}

/// <summary>Leaf controls share the Core property schema; unset values keep engine defaults.</summary>
public sealed class RulesEditorOption
{
    public required string Path { get; init; }
    public required Type ValueType { get; init; }
    public string Text { get; set; } = "";
    public string Hint => ValueType == typeof(string) ? "文本（留空使用默认）" :
        ValueType == typeof(bool) ? "使用默认时沿用分析引擎设置" :
        ValueType == typeof(object) ? "JSON 值，例如 0、\"文本\"、null" :
        ValueType.IsGenericType ? "JSON 数组，例如 [\"a\",\"b\"]" : "数字（留空使用默认）";
    public bool IsBoolean => ValueType == typeof(bool);
    public bool IsNumeric => ValueType == typeof(int) || ValueType == typeof(long) || ValueType == typeof(double);
    public bool IsInteger => ValueType == typeof(int) || ValueType == typeof(long);
    public double EditorWidth => IsNumeric ? 150 : HasChoices ? 210 : 300;
    public bool HasChoices => IsBoolean || ChoiceValues.Length > 0;
    private string[] ChoiceValues => Path switch
    {
        "primaryType" => ["String", "Number", "Boolean", "DateTime", "Object", "Array", "Null"],
        "transform.type" => ["jwt", "aes", "sm4", "sm2", "base64", "url"],
        "transform.mode" => ["cbc", "ecb", "decrypt", "verify"],
        "transform.padding" => ["pkcs7", "none"],
        "transform.format" => ["base64", "hex"],
        "transform.parse" => ["json"],
        "parseType" => ["none", "auto", "json", "json-string"],
        "memory.mode" => ["soft", "hard"],
        "pii.failOnLevel" => ["A", "B", "C"],
        _ => []
    };
    public IReadOnlyList<RulesEditorChoice> Choices
    {
        get
        {
            var choices = new List<RulesEditorChoice> { new("", "使用默认") };
            if (IsBoolean) choices.AddRange([new("true", "启用"), new("false", "关闭")]);
            else choices.AddRange(ChoiceValues.Select(v => new RulesEditorChoice(v, v)));
            // Preserve imported extensions rather than silently clearing their value in a selector.
            if (!string.IsNullOrEmpty(Text) && !choices.Any(c => c.Value == Text))
                choices.Add(new(Text, Text + "（导入值）"));
            return choices;
        }
    }

    public static bool AcceptsNumericInput(string text, bool wholeNumbersOnly) => Regex.IsMatch(text,
        wholeNumbersOnly ? @"^[+-]?[0-9]*$" : @"^[+-]?(?:[0-9]+(?:\.[0-9]*)?(?:[eE][+-]?[0-9]*)?|\.(?:[0-9]+(?:[eE][+-]?[0-9]*)?)?)?$",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    public string DisplayName => Labels.GetValueOrDefault(Path.Split('.')[^1], Path.Split('.')[^1]);
    public string Label => Labels.GetValueOrDefault(Path.Split('.')[^1], Path.Split('.')[^1]) + "（" + Path + "）";
    private static readonly Dictionary<string, string> Labels = new()
    {
        ["csvInferNumbers"] = "推断 CSV 数字", ["maxValuesToShow"] = "展示值数量", ["maxTrackedValues"] = "跟踪唯一值上限",
        ["maxParallelism"] = "并行度（-1 为全部核心）", ["allowUnknownRules"] = "允许未知规则", ["selectedFields"] = "选择字段",
        ["filter"] = "过滤表达式", ["reservoir"] = "随机采样数量", ["step"] = "采样步长", ["root"] = "根路径", ["record"] = "记录路径",
        ["snapshot"] = "快照路径", ["diffBaseline"] = "差异基线", ["diffReport"] = "差异报告", ["allowBreakingChanges"] = "允许破坏性变更",
        ["lowConfidenceWidth"] = "置信区间宽度", ["off"] = "关闭敏感信息识别", ["report"] = "报告路径", ["failOnLevel"] = "合规门禁等级（A/B/C）",
        ["failOnViolations"] = "违规时阻断", ["failOnOutlierRatio"] = "异常值比例门槛",
        ["failOnOverallScoreBelow"] = "总分下限", ["failOnCompletenessBelow"] = "完整性下限", ["failOnValidityBelow"] = "有效性下限",
        ["failOnUniquenessBelow"] = "唯一性下限", ["failOnConsistencyBelow"] = "一致性下限", ["failOnAnomalyControlBelow"] = "异常控制下限",
        ["correlation"] = "相关性分析", ["stringPatterns"] = "字符串模式", ["unicodeHygiene"] = "Unicode 检查",
        ["timeSeries"] = "时间序列", ["distributionSnapshot"] = "自动分布快照", ["fuzzyDedupField"] = "模糊去重字段",
        ["path"] = "检查点路径", ["resumeFrom"] = "恢复检查点", ["baseline"] = "历史结果基线", ["maxMB"] = "内存上限 MB",
        ["enabled"] = "启用", ["log"] = "日志路径", ["operator"] = "操作人", ["primary"] = "主报告路径",
        ["violations"] = "违规报告路径", ["quality"] = "质量报告路径", ["schemaCode"] = "代码导出（如 csharp:out.cs）",
        ["defaultValue"] = "默认值", ["primaryType"] = "主导类型", ["enumValues"] = "枚举值",
        ["maxOutlierRatio"] = "异常值比例上限", ["redact"] = "脱敏（默认自动）", ["type"] = "转换类型",
        ["claim"] = "JWT 声明", ["key"] = "密钥", ["iv"] = "初始化向量", ["mode"] = "工作模式",
        ["padding"] = "填充（pkcs7/none）", ["format"] = "编码（base64/hex）", ["privateKey"] = "私钥", ["publicKey"] = "公钥",
        ["signature"] = "签名", ["parse"] = "转换后解析（json）", ["label"] = "敏感信息标签", ["highSeverity"] = "高严重度",
        ["keepPrefix"] = "保留前缀长度", ["keepSuffix"] = "保留后缀长度", ["maskChar"] = "打码字符", ["maskAll"] = "全部打码",
        ["source"] = "来源列", ["parseType"] = "解析类型", ["flatten"] = "展开嵌套对象", ["header"] = "表头行号"
    };
    public static List<RulesEditorOption> ForType(Type type, JsonObject source, string[]? exclude = null, string prefix = "")
    {
        var result = new List<RulesEditorOption>();
        foreach (var property in type.GetProperties())
        {
            var name = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name;
            if (name is null || exclude?.Contains(name) == true) continue;
            var path = prefix + name;
            var valueType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            var node = Find(source, name);
            if (valueType == typeof(FieldTransform) || valueType == typeof(PiiRule) || valueType == typeof(PiiMaskConfig))
                result.AddRange(ForType(valueType, node as JsonObject ?? new(), prefix: path + "."));
            else result.Add(Create(path, valueType, node));
        }
        return result;
    }

    public static List<RulesEditorOption> Runtime(JsonObject source)
    {
        var result = new List<RulesEditorOption>();
        foreach (var (group, children) in RulesToOptionsConverter.KnownRuntimeKeys)
        {
            foreach (var path in children is null ? new[] { group } : children.Select(c => group + "." + c))
            {
                if (path == "flatten") continue;
                var type = RuntimeType(path);
                JsonNode? node = source;
                foreach (var part in path.Split('.')) node = node is JsonObject obj ? Find(obj, part) : null;
                result.Add(Create(path, type, node));
            }
        }
        return result;
    }

    private static Type RuntimeType(string path) => path switch
    {
        "csvInferNumbers" or "allowUnknownRules" or "schema.allowBreakingChanges" or "pii.off" or
        "gates.failOnViolations" or "deepAnalysis.correlation" or "deepAnalysis.stringPatterns" or
        "deepAnalysis.unicodeHygiene" or "deepAnalysis.timeSeries" or "deepAnalysis.distributionSnapshot" or
        "progress.enabled" => typeof(bool),
        "maxValuesToShow" or "maxTrackedValues" or "maxParallelism" or "sampling.reservoir" or "sampling.step" => typeof(int),
        "memory.maxMB" => typeof(long),
        "selectedFields" => typeof(List<string>),
        _ when path.StartsWith("gates.", StringComparison.Ordinal) || path == "distribution.lowConfidenceWidth" => typeof(double),
        _ => typeof(string)
    };

    private static RulesEditorOption Create(string path, Type type, JsonNode? node) => new()
    {
        Path = path, ValueType = type,
        Text = node is null ? "" : type == typeof(string) ? node.GetValue<string>() : node.ToJsonString()
    };
    private static JsonNode? Find(JsonObject obj, string name) => obj.FirstOrDefault(p =>
        string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    public static void Apply(JsonObject target, IEnumerable<RulesEditorOption> options)
    {
        foreach (var option in options)
        {
            JsonNode? value = null;
            if (string.IsNullOrWhiteSpace(option.Text))
            {
                JsonNode? existing = target;
                foreach (var part in option.Path.Split('.'))
                    existing = existing is JsonObject obj ? Find(obj, part) : null;
                // Empty objects can enable default behavior (e.g. pii: {}); leave them intact.
                if (existing is null) continue;
            }
            if (!string.IsNullOrWhiteSpace(option.Text))
            {
                try
                {
                    value = option.ValueType == typeof(string) ? JsonValue.Create(option.Text) :
                        option.ValueType == typeof(int) ? JsonValue.Create(int.Parse(option.Text, NumberStyles.Integer, CultureInfo.InvariantCulture)) :
                        option.ValueType == typeof(long) ? JsonValue.Create(long.Parse(option.Text, NumberStyles.Integer, CultureInfo.InvariantCulture)) :
                        option.ValueType == typeof(double) ? JsonValue.Create(ParseFiniteNumber(option.Text)) : JsonNode.Parse(option.Text);
                    if (value is not null && option.ValueType != typeof(object))
                        _ = JsonSerializer.Deserialize(value.ToJsonString(), option.ValueType);
                }
                catch (Exception ex) when (ex is JsonException or FormatException or OverflowException)
                { throw new InvalidOperationException($"{option.Path}：{option.Hint}", ex); }
            }
            SetPath(target, option.Path.Split('.'), 0, value);
        }
    }

    private static double ParseFiniteNumber(string text)
    {
        var value = double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
        return double.IsFinite(value) ? value : throw new FormatException("必须为有限数字。");
    }

    private static void SetPath(JsonObject target, string[] parts, int index, JsonNode? value)
    {
        var key = target.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, parts[index], StringComparison.OrdinalIgnoreCase)) ?? parts[index];
        if (index == parts.Length - 1)
        {
            target.Remove(key);
            if (value is not null) target[parts[index]] = value;
            return;
        }
        var child = target[key] as JsonObject;
        if (child is null && value is null) return;
        child ??= new JsonObject();
        SetPath(child, parts, index + 1, value);
        target.Remove(key);
        if (child.Count > 0) target[parts[index]] = child;
    }
}
