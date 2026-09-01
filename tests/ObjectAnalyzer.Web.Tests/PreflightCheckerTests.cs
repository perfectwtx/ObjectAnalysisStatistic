using ObjectAnalyzer.Analysis;
using ObjectAnalyzer.Parsing;

namespace ObjectAnalyzer.Web.Tests;

/// <summary>
/// PreflightChecker 单元测试：覆盖"规则字段 vs 数据样本字段"匹配逻辑的全部判定分支。
/// Build 入口无需落盘，单测重点验证纯计算；Check 入口写一个临时 JSON 文件以验证文件 IO 路径。
/// </summary>
public class PreflightCheckerTests
{
    // 共享样本数组（避免 CA1861 重复分配）
    private static readonly string[] EmptyStringArray = Array.Empty<string>();
    private static readonly string[] TwoFakeFields = new[] { "a", "b" };
    private static readonly string[] ABFields = new[] { "a", "b" };
    private static readonly string[] AFields = new[] { "a" };
    private static readonly string[] WhitespaceOnlyRuleFields = new[] { "", "  " };
    private static readonly string[] LineRuleFields = new[] { "line", "line.body" };
    private static readonly string[] ABCSampleFields = new[] { "A", "B", "C" };
    private static readonly string[] NameAgeRuleFields = new[] { "name", "age" };
    private static readonly string[] NameAgeExtraSample = new[] { "name", "age", "email" };
    private static readonly string[] CasedRuleFields = new[] { "Name", "AGE" };
    private static readonly string[] LowerCasedSampleFields = new[] { "name", "age" };
    private static readonly string[] DedupRuleFields = new[] { "a", "b", "a", "c", "b" };
    private static readonly string[] ABCDedupSample = new[] { "a", "b", "c" };
    private static readonly string[] NameRuleFields = new[] { "name" };
    private static readonly string[] NameSampleOnly = new[] { "name" };
    private static readonly string[] NameLineRuleFields = new[] { "name", "line" };
    private static readonly string[] NameAgeSampleFields = new[] { "name", "age" };
    private static readonly string[] MissingRuleFields = new[] { "missing" };
    private static readonly string[] LineJsonLines = new[] { "{\"line\":\"hello\"}", "{\"line\":\"world\"}" };
    private static readonly string[] NameOtherJsonLines = new[] { "{\"name\":\"a\",\"other\":1}", "{\"name\":\"b\",\"other\":2}" };
    private static readonly string[] TwoFakeNameAgeJsonLines = new[] { "{\"name\":\"a\"}", "{\"age\":1}" };
    private static readonly string[] OneNameJsonLine = new[] { "{\"name\":\"a\"}" };
    private static readonly string[] F0ToF4 = new[] { "f0", "f1", "f2", "f3", "f4" };

    // ──────────────────────────────────────────────
    // Build 纯计算入口：null / 空集合 / 重复 / 大小写 / 截断
    // ──────────────────────────────────────────────

    [Fact]
    [Trait("Category", "Preflight")]
    public void Build_NullOrEmptyRuleFields_NoWarning()
    {
        var r1 = PreflightChecker.Build(EmptyStringArray, ABFields);
        Assert.False(r1.HasWarning);
        Assert.Empty(r1.RuleFields);

        var r2 = PreflightChecker.Build(WhitespaceOnlyRuleFields, AFields);
        Assert.False(r2.HasWarning);
        Assert.Empty(r2.RuleFields);
    }

    [Fact]
    [Trait("Category", "Preflight")]
    public void Build_EmptySampleFields_NoWarningEvenIfRulesHaveFields()
    {
        // 拿不到数据样本 → 留给正式分析自己报错；预检不阻断
        var result = PreflightChecker.Build(TwoFakeFields, EmptyStringArray);
        Assert.False(result.HasWarning);
        Assert.Equal(2, result.RuleFields.Count);
        Assert.Empty(result.SampleFields);
    }

    [Fact]
    [Trait("Category", "Preflight")]
    public void Build_NoOverlap_TriggersWarning()
    {
        var result = PreflightChecker.Build(LineRuleFields, ABCSampleFields);

        Assert.True(result.HasWarning);
        Assert.Equal(LineRuleFields, result.UnmatchedRuleFields);
        Assert.Empty(result.MatchedRuleFields);
        Assert.Equal(ABCSampleFields, result.SampleFields);
        Assert.Equal(0, result.TruncatedSampleFieldCount);
    }

    [Fact]
    [Trait("Category", "Preflight")]
    public void Build_PartialOverlap_NoWarning()
    {
        // 部分匹配：至少 1 个规则字段落到数据里 → 用户用对了规则文件，不阻断
        var result = PreflightChecker.Build(NameLineRuleFields, NameAgeSampleFields);

        Assert.False(result.HasWarning);
        Assert.Single(result.MatchedRuleFields);
        Assert.Contains("name", result.MatchedRuleFields);
        Assert.Single(result.UnmatchedRuleFields);
        Assert.Contains("line", result.UnmatchedRuleFields);
    }

    [Fact]
    [Trait("Category", "Preflight")]
    public void Build_FullOverlap_NoWarning()
    {
        var result = PreflightChecker.Build(NameAgeRuleFields, NameAgeExtraSample);
        Assert.False(result.HasWarning);
        Assert.Equal(2, result.MatchedRuleFields.Count);
        Assert.Empty(result.UnmatchedRuleFields);
    }

    [Fact]
    [Trait("Category", "Preflight")]
    public void Build_CaseInsensitive()
    {
        // 大小写不敏感：name ≡ Name ≡ NAME
        var result = PreflightChecker.Build(CasedRuleFields, LowerCasedSampleFields);
        Assert.False(result.HasWarning);
        Assert.Equal(2, result.MatchedRuleFields.Count);
    }

    [Fact]
    [Trait("Category", "Preflight")]
    public void Build_DedupesRuleFields_PreservesInsertionOrder()
    {
        var result = PreflightChecker.Build(DedupRuleFields, ABCDedupSample);
        Assert.Equal(ABCDedupSample, result.RuleFields);
    }

    [Fact]
    [Trait("Category", "Preflight")]
    public void Build_DedupesSampleFields_TruncatesAtMaxDisplayed()
    {
        // 构造 25 个样本字段 → 限长 20 应生效
        var sample = Enumerable.Range(0, 25).Select(i => $"f{i}").ToArray();
        var result = PreflightChecker.Build(MissingRuleFields, sample);

        Assert.Equal(20, result.SampleFields.Count);
        Assert.Equal(5, result.TruncatedSampleFieldCount);
        Assert.Equal("f0", result.SampleFields[0]);
        Assert.Equal("f19", result.SampleFields[19]);
    }

    [Fact]
    [Trait("Category", "Preflight")]
    public void Build_SampleFieldsTruncationNotTriggered_WhenUnderLimit()
    {
        var sample = Enumerable.Range(0, 5).Select(i => $"f{i}").ToArray();
        var result = PreflightChecker.Build(MissingRuleFields, sample);
        Assert.Equal(5, result.SampleFields.Count);
        Assert.Equal(0, result.TruncatedSampleFieldCount);
    }

    [Fact]
    [Trait("Category", "Preflight")]
    public void Build_SkipsEmptySampleFieldNames()
    {
        var result = PreflightChecker.Build(NameRuleFields, NameSampleOnly);
        Assert.False(result.HasWarning);
        Assert.Single(result.SampleFields);
    }

    [Fact]
    [Trait("Category", "Preflight")]
    public void NoWarning_HasEmptyCollections()
    {
        var result = PreflightResult.NoWarning();
        Assert.False(result.HasWarning);
        Assert.Empty(result.RuleFields);
        Assert.Empty(result.MatchedRuleFields);
        Assert.Empty(result.UnmatchedRuleFields);
        Assert.Empty(result.SampleFields);
    }

    // ──────────────────────────────────────────────
    // Check 入口：文件 IO 路径（写临时 JSONL 文件）
    // ──────────────────────────────────────────────

    [Fact]
    [Trait("Category", "Preflight")]
    public void Check_NullRules_NoWarning()
    {
        var path = WriteTempJsonl(TwoFakeNameAgeJsonLines);
        try
        {
            var result = PreflightChecker.Check(path, rules: null, new ParseOptions(), "jsonl");
            Assert.False(result.HasWarning);
        }
        finally { TryDelete(path); }
    }

    [Fact]
    [Trait("Category", "Preflight")]
    public void Check_EmptyRulesFields_NoWarning()
    {
        var path = WriteTempJsonl(OneNameJsonLine);
        try
        {
            var rules = new AnalysisRules(); // Fields 为空字典
            var result = PreflightChecker.Check(path, rules, new ParseOptions(), "jsonl");
            Assert.False(result.HasWarning);
        }
        finally { TryDelete(path); }
    }

    [Fact]
    [Trait("Category", "Preflight")]
    public void Check_JsonlNoOverlap_TriggersWarning()
    {
        // 数据是 line 字段 → 规则里是 name / age → 完全不匹配
        var path = WriteTempJsonl(LineJsonLines);
        try
        {
            var rules = new AnalysisRules
            {
                Fields = new Dictionary<string, FieldRule>
                {
                    ["name"] = new FieldRule(),
                    ["age"] = new FieldRule()
                }
            };
            var result = PreflightChecker.Check(path, rules, new ParseOptions(), "jsonl");
            Assert.True(result.HasWarning);
            Assert.Equal(2, result.UnmatchedRuleFields.Count);
        }
        finally { TryDelete(path); }
    }

    [Fact]
    [Trait("Category", "Preflight")]
    public void Check_JsonlPartialOverlap_NoWarning()
    {
        var path = WriteTempJsonl(NameOtherJsonLines);
        try
        {
            var rules = new AnalysisRules
            {
                Fields = new Dictionary<string, FieldRule>
                {
                    ["name"] = new FieldRule(),
                    ["age"] = new FieldRule()
                }
            };
            var result = PreflightChecker.Check(path, rules, new ParseOptions(), "jsonl");
            Assert.False(result.HasWarning);
            Assert.Contains("name", result.MatchedRuleFields);
            Assert.Contains("age", result.UnmatchedRuleFields);
        }
        finally { TryDelete(path); }
    }

    [Fact]
    [Trait("Category", "Preflight")]
    public void Check_FileMissing_NoWarning()
    {
        // 文件不存在 → Check 内部 try/catch 降级为 no warning（不阻断 UI）
        var rules = new AnalysisRules
        {
            Fields = new Dictionary<string, FieldRule>
            {
                ["name"] = new FieldRule()
            }
        };
        var result = PreflightChecker.Check(@"C:\__definitely__\not\here.jsonl", rules, new ParseOptions(), "jsonl");
        Assert.False(result.HasWarning);
    }

    [Fact]
    [Trait("Category", "Preflight")]
    public void Check_SampleSizeBoundsCollection()
    {
        // 写 30 条数据，sampleSize=5 → 只解析前 5 条收集字段
        var path = WriteTempJsonl(Enumerable.Range(0, 30).Select(i => $"{{\"f{i}\":1}}"));
        try
        {
            var rules = new AnalysisRules
            {
                Fields = new Dictionary<string, FieldRule>
                {
                    ["missing"] = new FieldRule()
                }
            };
            var result = PreflightChecker.Check(path, rules, new ParseOptions(), "jsonl", sampleSize: 5);
            Assert.True(result.HasWarning);
            // 样本只看到 f0..f4
            Assert.Equal(5, result.SampleFields.Count);
            Assert.Equal(F0ToF4, result.SampleFields);
        }
        finally { TryDelete(path); }
    }

    // ──────────────────────────────────────────────
    // 工具方法
    // ──────────────────────────────────────────────

    private static string WriteTempJsonl(IEnumerable<string> lines)
    {
        var path = Path.Combine(Path.GetTempPath(), $"preflight-test-{Guid.NewGuid():N}.jsonl");
        File.WriteAllLines(path, lines);
        return path;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* 忽略清理异常 */ }
    }
}
