namespace ObjectAnalyzer.Tests;

/// <summary>
/// PiiPolicy 出站打码策略（一次解析、处处复用）与 PreviewRedactor 预览打码测试。
/// 策略解析优先级：规则 redact:false 白名单 → 规则 pii 声明 → 内置语义类型。
/// </summary>
public class PiiPolicyTests
{
    private static readonly List<Dictionary<string, object?>> Sample = new()
    {
        new() { { "Phone", "13812341234" }, { "City", "Beijing" } },
        new() { { "Phone", "13900001111" }, { "City", "Shanghai" } }
    };

    private static readonly List<Dictionary<string, object?>> CleanSample = new()
    {
        new() { { "Id", 1 }, { "Active", true } },
        new() { { "Id", 2 }, { "Active", false } }
    };

    // ──────────────────────────────────────────────
    // 策略解析
    // ──────────────────────────────────────────────

    [Fact]
    public void Resolve_BuiltinSemantic_ProducesBuiltinMask()
    {
        var policy = PiiPolicy.Resolve(Analyze(Sample));

        Assert.False(policy.IsEmpty);
        Assert.Equal(1, policy.FieldCount);
        Assert.False(policy.MasksByField["Phone"].IsCustom);
        Assert.Equal("138****1234", policy.Apply("Phone", "13812341234"));
    }

    [Fact]
    public void Resolve_CustomRulePii_UsesDeclaredFormat()
    {
        var rules = new AnalysisRules();
        rules.Fields["internalId"] = new FieldRule
        {
            Pii = new PiiRule { Label = "内部标识", Mask = new PiiMaskConfig { KeepPrefix = 2, KeepSuffix = 2 } }
        };
        var objects = new List<Dictionary<string, object?>>
        {
            new() { { "internalId", "ID-1001" } }
        };

        var policy = PiiPolicy.Resolve(Analyze(objects, rules));

        Assert.True(policy.MasksByField["internalId"].IsCustom);
        Assert.Equal("内部标识", policy.MasksByField["internalId"].Label);
        Assert.Equal("ID***01", policy.Apply("internalId", "ID-1001"));
    }

    [Fact]
    public void Resolve_RedactFalseWhitelist_ExcludesField()
    {
        // 语义命中但白名单豁免 → 不进策略
        var rules = new AnalysisRules();
        rules.Fields["name"] = new FieldRule { Redact = false };
        var objects = new List<Dictionary<string, object?>>
        {
            new() { { "name", "超级无敌洗发水" }, { "Phone", "13812341234" } }
        };

        var policy = PiiPolicy.Resolve(Analyze(objects, rules));

        Assert.False(policy.MasksByField.ContainsKey("name"));
        Assert.True(policy.MasksByField.ContainsKey("Phone"));
        // 白名单字段原样出站
        Assert.Equal("超级无敌洗发水", policy.Apply("name", "超级无敌洗发水"));
    }

    [Fact]
    public void Resolve_CleanData_ReturnsEmpty()
    {
        var policy = PiiPolicy.Resolve(Analyze(CleanSample));

        Assert.True(policy.IsEmpty);
        Assert.Same(PiiPolicy.Empty, policy);
    }

    [Fact]
    public void Apply_NonPiiFieldOrNull_PassesThrough()
    {
        var policy = PiiPolicy.Resolve(Analyze(Sample));

        Assert.Equal("Beijing", policy.Apply("City", "Beijing"));
        Assert.Null(policy.Apply("Phone", null));
        Assert.Null(policy.Apply("Unknown", null));
    }

    // ──────────────────────────────────────────────
    // PreviewRedactor：预览出站打码
    // ──────────────────────────────────────────────

    [Fact]
    public void PreviewRedactor_MasksPiiFields_WithoutMutatingInput()
    {
        var policy = PiiPolicy.Resolve(Analyze(Sample));
        var preview = new List<Dictionary<string, object?>>
        {
            new() { { "Phone", "13812341234" }, { "City", "Beijing" } }
        };

        var redacted = PreviewRedactor.Redact(preview, policy);

        Assert.Equal("138****1234", redacted[0]["Phone"]);
        Assert.Equal("Beijing", redacted[0]["City"]);
        // 原始预览未被修改（打码必须生成副本）
        Assert.Equal("13812341234", preview[0]["Phone"]);
    }

    [Fact]
    public void PreviewRedactor_NumericScalar_ConvertsThenMasks()
    {
        // 被解析成数值的手机号同样不能以原文出站：转字符串后按策略打码
        var policy = PiiPolicy.Resolve(Analyze(Sample));
        var preview = new List<Dictionary<string, object?>>
        {
            new() { { "Phone", 13900001111L } }
        };

        var redacted = PreviewRedactor.Redact(preview, policy);

        Assert.Equal("139****1111", redacted[0]["Phone"]);
    }

    [Fact]
    public void PreviewRedactor_RowWithoutPii_DetachesRow()
    {
        var policy = PiiPolicy.Resolve(Analyze(Sample));
        var cleanRow = new Dictionary<string, object?> { { "City", "Beijing" } };
        var preview = new List<Dictionary<string, object?>> { cleanRow };

        var redacted = PreviewRedactor.Redact(preview, policy);

        Assert.NotSame(cleanRow, redacted[0]);
    }

    [Fact]
    public void PreviewRedactor_EmptyPolicy_DetachesPreview()
    {
        var preview = new List<Dictionary<string, object?>>
        {
            new() { { "City", "Beijing" } }
        };

        Assert.NotSame(preview, PreviewRedactor.Redact(preview, PiiPolicy.Empty));
    }

    // ──────────────────────────────────────────────
    // PiiRedactor 消费策略：与 bool 重载行为一致
    // ──────────────────────────────────────────────

    [Fact]
    public void PiiRedactor_PolicyOverload_MatchesBoolOverload()
    {
        var result = Analyze(Sample);

        var viaPolicy = PiiRedactor.Redact(result, PiiPolicy.Resolve(result));
        var viaBool = PiiRedactor.Redact(result);

        Assert.Equal(
            Stat(viaBool, "Phone").ValueCounts.Keys.OrderBy(k => k),
            Stat(viaPolicy, "Phone").ValueCounts.Keys.OrderBy(k => k));
        Assert.Contains("138****1234", Stat(viaPolicy, "Phone").ValueCounts.Keys);
    }

    [Fact]
    public void PiiRedactor_EmptyPolicy_DetachesResult()
    {
        var result = Analyze(CleanSample);

        Assert.NotSame(result, PiiRedactor.Redact(result, PiiPolicy.Resolve(result)));
    }

    private static FieldStatistic Stat(AnalysisResult result, string fieldName)
        => result.FieldStatistics.First(f => f.FieldName == fieldName);
}
