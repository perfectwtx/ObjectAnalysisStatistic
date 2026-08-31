using System.Text.Json;

namespace ObjectAnalyzer.Tests;

/// <summary>
/// <see cref="AnalysisRuntime"/> 及其 12 个嵌套配置类的纯数据行为测试：
/// <list type="bullet">
///   <item>构造时所有属性为 <c>null</c>（缺省 = 沿用 <c>AnalysisOptions</c> 默认）</item>
///   <item>JSON 短名（camelCase）正确映射到 PascalCase 属性</item>
///   <item>嵌套节可独立读写，序列化时保持分组结构</item>
///   <item>完整 JSON 往返一致</item>
///   <item>未知键被 System.Text.Json 静默忽略（lint 责任另由 <c>RulesLint</c> 负责）</item>
/// </list>
/// 本测试不验证与 <c>AnalysisOptions</c> / <c>AnalysisRules.Runtime</c> / <c>ApplyRuntime</c> 的集成——那些由后续 PR 单独覆盖。
/// </summary>
public class AnalysisRuntimeTests
{
    // 复用：CA1861 提示每次 new[] 会分配新数组，提到静态只读字段
    private static readonly string[] NameAge = { "name", "age" };
    private static readonly string[] Abc = { "a", "b", "c" };

    // ── A. 构造默认值 ──

    [Fact]
    public void AnalysisRuntime_Default_AllPropertiesAreNull()
    {
        var rt = new AnalysisRuntime();

        // 解析行为
        Assert.Null(rt.Flatten);
        Assert.Null(rt.CsvInferNumbers);
        Assert.Null(rt.MaxValuesToShow);
        Assert.Null(rt.MaxParallelism);
        Assert.Null(rt.AllowUnknownRules);

        // 字段选择
        Assert.Null(rt.SelectedFields);
        Assert.Null(rt.Filter);

        // 嵌套节（全部为 null，调用方按需 new）
        Assert.Null(rt.Sampling);
        Assert.Null(rt.JsonPath);
        Assert.Null(rt.Schema);
        Assert.Null(rt.Distribution);
        Assert.Null(rt.Pii);
        Assert.Null(rt.Gates);
        Assert.Null(rt.P4);
        Assert.Null(rt.Checkpoint);
        Assert.Null(rt.Memory);
        Assert.Null(rt.Progress);
        Assert.Null(rt.Audit);
        Assert.Null(rt.Outputs);
    }

    [Fact]
    public void SamplingConfig_Default_AllPropertiesAreNull()
    {
        var c = new SamplingConfig();
        Assert.Null(c.Reservoir);
        Assert.Null(c.Step);
    }

    [Fact]
    public void JsonPathConfig_Default_AllPropertiesAreNull()
    {
        var c = new JsonPathConfig();
        Assert.Null(c.Root);
        Assert.Null(c.Record);
    }

    [Fact]
    public void SchemaConfig_Default_AllPropertiesAreNull()
    {
        var c = new SchemaConfig();
        Assert.Null(c.Snapshot);
        Assert.Null(c.DiffBaseline);
        Assert.Null(c.DiffReport);
        Assert.Null(c.AllowBreakingChanges);
    }

    [Fact]
    public void DistributionConfig_Default_AllPropertiesAreNull()
    {
        var c = new DistributionConfig();
        Assert.Null(c.Snapshot);
        Assert.Null(c.DiffBaseline);
        Assert.Null(c.DiffReport);
        Assert.Null(c.LowConfidenceWidth);
    }

    [Fact]
    public void PiiRuntimeConfig_Default_AllPropertiesAreNull()
    {
        var c = new PiiRuntimeConfig();
        Assert.Null(c.Off);
        Assert.Null(c.Report);
        Assert.Null(c.FailOnLevel);
    }

    [Fact]
    public void GatesConfig_Default_AllPropertiesAreNull()
    {
        var c = new GatesConfig();
        Assert.Null(c.FailOnViolations);
        Assert.Null(c.FailOnOutlierRatio);
    }

    [Fact]
    public void P4Config_Default_AllPropertiesAreNull()
    {
        var c = new P4Config();
        Assert.Null(c.Correlation);
        Assert.Null(c.StringPatterns);
        Assert.Null(c.UnicodeHygiene);
        Assert.Null(c.TimeSeries);
        Assert.Null(c.DistributionSnapshot);
        Assert.Null(c.FuzzyDedupField);
    }

    [Fact]
    public void CheckpointConfig_Default_AllPropertiesAreNull()
    {
        var c = new CheckpointConfig();
        Assert.Null(c.Path);
        Assert.Null(c.ResumeFrom);
        Assert.Null(c.Baseline);
    }

    [Fact]
    public void MemoryConfig_Default_AllPropertiesAreNull()
    {
        var c = new MemoryConfig();
        Assert.Null(c.MaxMB);
        Assert.Null(c.Mode);
    }

    [Fact]
    public void ProgressConfig_Default_AllPropertiesAreNull()
    {
        var c = new ProgressConfig();
        Assert.Null(c.Enabled);
    }

    [Fact]
    public void AuditConfig_Default_AllPropertiesAreNull()
    {
        var c = new AuditConfig();
        Assert.Null(c.Log);
        Assert.Null(c.Operator);
    }

    [Fact]
    public void OutputsConfig_Default_AllPropertiesAreNull()
    {
        var c = new OutputsConfig();
        Assert.Null(c.Primary);
        Assert.Null(c.Violations);
        Assert.Null(c.Quality);
        Assert.Null(c.SchemaCode);
    }

    // ── B. 序列化：PascalCase 属性 → camelCase 短名 ──

    [Fact]
    public void Serialize_UsesJsonPropertyNameShortNames()
    {
        var rt = new AnalysisRuntime
        {
            Flatten = true,
            CsvInferNumbers = true,
            MaxValuesToShow = 8,
            MaxParallelism = 4,
            AllowUnknownRules = true,
            SelectedFields = new List<string> { "name", "age" },
            Filter = "name == \"x\"",
            Sampling = new SamplingConfig { Reservoir = 100, Step = 5 },
            JsonPath = new JsonPathConfig { Root = "$.data[*]", Record = "$.payload" },
            Schema = new SchemaConfig { Snapshot = "snap.json", AllowBreakingChanges = true },
            Distribution = new DistributionConfig { Snapshot = "dist.json", LowConfidenceWidth = 0.15 },
            Pii = new PiiRuntimeConfig { Off = false, Report = "pii.json", FailOnLevel = "B" },
            Gates = new GatesConfig { FailOnViolations = true, FailOnOutlierRatio = 0.05 },
            P4 = new P4Config
            {
                Correlation = true,
                StringPatterns = true,
                UnicodeHygiene = true,
                TimeSeries = true,
                DistributionSnapshot = true,
                FuzzyDedupField = "name"
            },
            Checkpoint = new CheckpointConfig { Path = "run.snap", ResumeFrom = "prev.snap", Baseline = "base.json" },
            Memory = new MemoryConfig { MaxMB = 1024, Mode = "soft" },
            Progress = new ProgressConfig { Enabled = true },
            Audit = new AuditConfig { Log = "audit.ndjson", Operator = "[email protected]" },
            Outputs = new OutputsConfig
            {
                Primary = "report.html",
                Violations = "violations.json",
                Quality = "quality.json",
                SchemaCode = "csharp:out.cs"
            }
        };

        var json = JsonSerializer.Serialize(rt);

        // 顶层扁平键
        Assert.Contains("\"flatten\":true", json);
        Assert.Contains("\"csvInferNumbers\":true", json);
        Assert.Contains("\"maxValuesToShow\":8", json);
        Assert.Contains("\"maxParallelism\":4", json);
        Assert.Contains("\"allowUnknownRules\":true", json);
        Assert.Contains("\"selectedFields\":", json);
        Assert.Contains("\"filter\":", json);

        // 嵌套节
        Assert.Contains("\"sampling\":", json);
        Assert.Contains("\"reservoir\":100", json);
        Assert.Contains("\"step\":5", json);
        Assert.Contains("\"jsonPath\":", json);
        Assert.Contains("\"root\":\"$.data[*]\"", json);
        Assert.Contains("\"record\":\"$.payload\"", json);
        Assert.Contains("\"schema\":", json);
        Assert.Contains("\"snapshot\":\"snap.json\"", json);
        Assert.Contains("\"allowBreakingChanges\":true", json);
        Assert.Contains("\"distribution\":", json);
        Assert.Contains("\"lowConfidenceWidth\":0.15", json);
        Assert.Contains("\"pii\":", json);
        Assert.Contains("\"failOnLevel\":\"B\"", json);
        Assert.Contains("\"gates\":", json);
        Assert.Contains("\"failOnOutlierRatio\":0.05", json);
        Assert.Contains("\"p4\":", json);
        Assert.Contains("\"fuzzyDedupField\":\"name\"", json);
        Assert.Contains("\"checkpoint\":", json);
        Assert.Contains("\"resumeFrom\":\"prev.snap\"", json);
        Assert.Contains("\"memory\":", json);
        Assert.Contains("\"maxMB\":1024", json);
        Assert.Contains("\"progress\":", json);
        Assert.Contains("\"enabled\":true", json);
        Assert.Contains("\"audit\":", json);
        Assert.Contains("\"operator\":\"[email protected]\"", json);
        Assert.Contains("\"outputs\":", json);
        Assert.Contains("\"schemaCode\":\"csharp:out.cs\"", json);

        // PascalCase 不应泄漏
        Assert.DoesNotContain("\"Flatten\":", json);
        Assert.DoesNotContain("\"Sampling\":", json);
        Assert.DoesNotContain("\"Memory\":", json);
        Assert.DoesNotContain("\"MaxMB\":", json);
        Assert.DoesNotContain("\"Audit\":", json);
    }

    // ── C. 反序列化：camelCase 短名 → PascalCase 属性 ──

    [Fact]
    public void Deserialize_AllShortNames_MapToPascalCaseProperties()
    {
        var json = """
        {
          "flatten": true,
          "csvInferNumbers": true,
          "maxValuesToShow": 10,
          "maxParallelism": 4,
          "allowUnknownRules": true,
          "selectedFields": ["name", "age"],
          "filter": "name == \"x\""
        }
        """;

        var rt = JsonSerializer.Deserialize<AnalysisRuntime>(json)!;

        Assert.True(rt.Flatten);
        Assert.True(rt.CsvInferNumbers);
        Assert.Equal(10, rt.MaxValuesToShow);
        Assert.Equal(4, rt.MaxParallelism);
        Assert.True(rt.AllowUnknownRules);
        Assert.Equal(NameAge, rt.SelectedFields);
        Assert.Equal("name == \"x\"", rt.Filter);
    }

    [Fact]
    public void Deserialize_NestedSections_MapIndependently()
    {
        var json = """
        {
          "sampling":  { "reservoir": 100, "step": 5 },
          "jsonPath":  { "root": "$.data[*]", "record": "$.payload" },
          "schema":    { "snapshot": "snap.json", "diffBaseline": "prev.json", "diffReport": "diff.json", "allowBreakingChanges": false },
          "distribution": { "snapshot": "dist.json", "diffBaseline": "d0.json", "diffReport": "dd.json", "lowConfidenceWidth": 0.3 },
          "pii":       { "off": true, "report": "pii.json", "failOnLevel": "A" },
          "gates":     { "failOnViolations": true, "failOnOutlierRatio": 0.1 },
          "p4":        { "correlation": true, "stringPatterns": true, "unicodeHygiene": true, "timeSeries": true, "distributionSnapshot": true, "fuzzyDedupField": "title" },
          "checkpoint":{ "path": "run.snap", "resumeFrom": "prev.snap", "baseline": "base.json" },
          "memory":    { "maxMB": 2048, "mode": "hard" },
          "progress":  { "enabled": false },
          "audit":     { "log": "audit.ndjson", "operator": "ci-bot" },
          "outputs":   { "primary": "report.html", "violations": "v.json", "quality": "q.json", "schemaCode": "typescript:out.ts" }
        }
        """;

        var rt = JsonSerializer.Deserialize<AnalysisRuntime>(json)!;

        Assert.Equal(100, rt.Sampling!.Reservoir);
        Assert.Equal(5, rt.Sampling.Step);
        Assert.Equal("$.data[*]", rt.JsonPath!.Root);
        Assert.Equal("$.payload", rt.JsonPath.Record);
        Assert.Equal("snap.json", rt.Schema!.Snapshot);
        Assert.Equal("prev.json", rt.Schema.DiffBaseline);
        Assert.Equal("diff.json", rt.Schema.DiffReport);
        Assert.False(rt.Schema.AllowBreakingChanges);
        Assert.Equal("dist.json", rt.Distribution!.Snapshot);
        Assert.Equal(0.3, rt.Distribution.LowConfidenceWidth);
        Assert.True(rt.Pii!.Off);
        Assert.Equal("pii.json", rt.Pii.Report);
        Assert.Equal("A", rt.Pii.FailOnLevel);
        Assert.True(rt.Gates!.FailOnViolations);
        Assert.Equal(0.1, rt.Gates.FailOnOutlierRatio);
        Assert.True(rt.P4!.Correlation);
        Assert.True(rt.P4.StringPatterns);
        Assert.True(rt.P4.UnicodeHygiene);
        Assert.True(rt.P4.TimeSeries);
        Assert.True(rt.P4.DistributionSnapshot);
        Assert.Equal("title", rt.P4.FuzzyDedupField);
        Assert.Equal("run.snap", rt.Checkpoint!.Path);
        Assert.Equal("prev.snap", rt.Checkpoint.ResumeFrom);
        Assert.Equal("base.json", rt.Checkpoint.Baseline);
        Assert.Equal(2048L, rt.Memory!.MaxMB);
        Assert.Equal("hard", rt.Memory.Mode);
        Assert.False(rt.Progress!.Enabled);
        Assert.Equal("audit.ndjson", rt.Audit!.Log);
        Assert.Equal("ci-bot", rt.Audit.Operator);
        Assert.Equal("report.html", rt.Outputs!.Primary);
        Assert.Equal("v.json", rt.Outputs.Violations);
        Assert.Equal("q.json", rt.Outputs.Quality);
        Assert.Equal("typescript:out.ts", rt.Outputs.SchemaCode);
    }

    // ── D. 边界情况 ──

    [Fact]
    public void Deserialize_EmptyJson_AllNull()
    {
        var rt = JsonSerializer.Deserialize<AnalysisRuntime>("{}")!;

        Assert.Null(rt.Flatten);
        Assert.Null(rt.CsvInferNumbers);
        Assert.Null(rt.MaxValuesToShow);
        Assert.Null(rt.MaxParallelism);
        Assert.Null(rt.AllowUnknownRules);
        Assert.Null(rt.SelectedFields);
        Assert.Null(rt.Filter);
        Assert.Null(rt.Sampling);
        Assert.Null(rt.JsonPath);
        Assert.Null(rt.Schema);
        Assert.Null(rt.Distribution);
        Assert.Null(rt.Pii);
        Assert.Null(rt.Gates);
        Assert.Null(rt.P4);
        Assert.Null(rt.Checkpoint);
        Assert.Null(rt.Memory);
        Assert.Null(rt.Progress);
        Assert.Null(rt.Audit);
        Assert.Null(rt.Outputs);
    }

    [Fact]
    public void Deserialize_UnknownProperty_IsIgnored()
    {
        // System.Text.Json 默认对未知属性静默忽略；lint 由 RulesLint 单独负责
        var json = """
        {
          "flatten": true,
          "csvInferNumberz": true,
          "sampling": { "reservvoir": 100 },
          "extra": "ignored"
        }
        """;

        var rt = JsonSerializer.Deserialize<AnalysisRuntime>(json)!;

        Assert.True(rt.Flatten);
        Assert.Null(rt.CsvInferNumbers);
        Assert.Null(rt.Sampling!.Reservoir);
    }

    [Fact]
    public void Deserialize_NullValues_AllPropertiesStaysNull()
    {
        var json = """
        {
          "flatten": null,
          "maxValuesToShow": null,
          "sampling": null,
          "schema": { "snapshot": null, "allowBreakingChanges": null }
        }
        """;

        var rt = JsonSerializer.Deserialize<AnalysisRuntime>(json)!;

        Assert.Null(rt.Flatten);
        Assert.Null(rt.MaxValuesToShow);
        Assert.Null(rt.Sampling);
        Assert.NotNull(rt.Schema);
        Assert.Null(rt.Schema!.Snapshot);
        Assert.Null(rt.Schema.AllowBreakingChanges);
    }

    [Fact]
    public void Deserialize_FalseStaysFalseAndEmptyStaysEmpty()
    {
        // false / 0 / [] 都是"显式配置"，不能与 null（"未配置"）混淆
        var json = """
        {
          "flatten": false,
          "allowUnknownRules": false,
          "maxValuesToShow": 0,
          "selectedFields": []
        }
        """;

        var rt = JsonSerializer.Deserialize<AnalysisRuntime>(json)!;

        Assert.False(rt.Flatten);
        Assert.False(rt.AllowUnknownRules);
        Assert.Equal(0, rt.MaxValuesToShow);
        Assert.NotNull(rt.SelectedFields);
        Assert.Empty(rt.SelectedFields!);
    }

    // ── E. 完整往返 ──

    [Fact]
    public void RoundTrip_FullRuntime_PreservesAllValues()
    {
        var original = new AnalysisRuntime
        {
            Flatten = true,
            CsvInferNumbers = false,
            MaxValuesToShow = 12,
            MaxParallelism = 8,
            AllowUnknownRules = true,
            SelectedFields = new List<string> { "a", "b", "c" },
            Filter = "x >= 0",
            Sampling = new SamplingConfig { Reservoir = 500, Step = 10 },
            JsonPath = new JsonPathConfig { Root = "$.items[*]", Record = "$.data" },
            Schema = new SchemaConfig { Snapshot = "v1.json", DiffBaseline = "v0.json", DiffReport = "diff.json", AllowBreakingChanges = false },
            Distribution = new DistributionConfig { Snapshot = "d1.json", LowConfidenceWidth = 0.25 },
            Pii = new PiiRuntimeConfig { Off = true, Report = "compliance.json", FailOnLevel = "C" },
            Gates = new GatesConfig { FailOnViolations = true, FailOnOutlierRatio = 0.02 },
            P4 = new P4Config { Correlation = true, StringPatterns = false, TimeSeries = true, FuzzyDedupField = "name" },
            Checkpoint = new CheckpointConfig { Path = "run.snap", ResumeFrom = "p.snap", Baseline = "b.json" },
            Memory = new MemoryConfig { MaxMB = 4096, Mode = "soft" },
            Progress = new ProgressConfig { Enabled = false },
            Audit = new AuditConfig { Log = "audit.ndjson", Operator = "ci" },
            Outputs = new OutputsConfig { Primary = "r.html", Violations = "v.json", Quality = "q.json", SchemaCode = "csharp:o.cs" }
        };

        var json = JsonSerializer.Serialize(original);
        var restored = JsonSerializer.Deserialize<AnalysisRuntime>(json)!;

        // 顶层
        Assert.True(restored.Flatten);
        Assert.False(restored.CsvInferNumbers);
        Assert.Equal(12, restored.MaxValuesToShow);
        Assert.Equal(8, restored.MaxParallelism);
        Assert.True(restored.AllowUnknownRules);
        Assert.Equal(Abc, restored.SelectedFields);
        Assert.Equal("x >= 0", restored.Filter);

        // 嵌套节逐字段对照
        Assert.Equal(500, restored.Sampling!.Reservoir);
        Assert.Equal(10, restored.Sampling.Step);
        Assert.Equal("$.items[*]", restored.JsonPath!.Root);
        Assert.Equal("$.data", restored.JsonPath.Record);
        Assert.Equal("v1.json", restored.Schema!.Snapshot);
        Assert.Equal("v0.json", restored.Schema.DiffBaseline);
        Assert.Equal("diff.json", restored.Schema.DiffReport);
        Assert.False(restored.Schema.AllowBreakingChanges);
        Assert.Equal("d1.json", restored.Distribution!.Snapshot);
        Assert.Equal(0.25, restored.Distribution.LowConfidenceWidth);
        Assert.True(restored.Pii!.Off);
        Assert.Equal("compliance.json", restored.Pii.Report);
        Assert.Equal("C", restored.Pii.FailOnLevel);
        Assert.True(restored.Gates!.FailOnViolations);
        Assert.Equal(0.02, restored.Gates.FailOnOutlierRatio);
        Assert.True(restored.P4!.Correlation);
        Assert.False(restored.P4.StringPatterns);
        Assert.True(restored.P4.TimeSeries);
        Assert.Equal("name", restored.P4.FuzzyDedupField);
        Assert.Equal("run.snap", restored.Checkpoint!.Path);
        Assert.Equal("p.snap", restored.Checkpoint.ResumeFrom);
        Assert.Equal("b.json", restored.Checkpoint.Baseline);
        Assert.Equal(4096L, restored.Memory!.MaxMB);
        Assert.Equal("soft", restored.Memory.Mode);
        Assert.False(restored.Progress!.Enabled);
        Assert.Equal("audit.ndjson", restored.Audit!.Log);
        Assert.Equal("ci", restored.Audit.Operator);
        Assert.Equal("r.html", restored.Outputs!.Primary);
        Assert.Equal("v.json", restored.Outputs.Violations);
        Assert.Equal("q.json", restored.Outputs.Quality);
        Assert.Equal("csharp:o.cs", restored.Outputs.SchemaCode);
    }
}
