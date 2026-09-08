using System.Text.Json;
using System.Text.Json.Nodes;
using ObjectAnalyzer.Analysis;

namespace ObjectAnalyzer.Tests;

/// <summary>
/// <see cref="RulesToOptionsConverter"/> 的合并语义测试：
/// 验证 CLI/API 显式值 &gt; rules.runtime &gt; AnalysisOptions 内置默认的优先级链。
/// 规则文件以 JSON 形式提供，避免引入已删除的 AnalysisRuntime 强类型中间类。
/// </summary>
public class AnalysisOptionsApplyRuntimeTests
{
    private static AnalysisOptions NewDefaults() => new()
    {
        Input = { InputPath = "data.json" }
    };

    private static AnalysisRules ParseRules(string json) =>
        JsonSerializer.Deserialize<AnalysisRules>(json, AnalysisRules.RulesJsonOptions)
        ?? new AnalysisRules();

    private static void Apply(AnalysisOptions options, string runtimeJson)
    {
        var rules = new AnalysisRules { Runtime = JsonNode.Parse(runtimeJson) as JsonObject };
        RulesToOptionsConverter.Apply(options, rules);
    }

    [Fact]
    public void ApplyRuntime_NullRuntime_NoChange()
    {
        var options = NewDefaults();
        options.Parse.Flatten = false;
        options.Sampling.SampleReservoir = 500;

        RulesToOptionsConverter.Apply(options, null);

        Assert.False(options.Parse.Flatten);
        Assert.Equal(500, options.Sampling.SampleReservoir);
    }

    [Fact]
    public void ApplyRuntime_EmptyRuntime_NoChange()
    {
        var options = NewDefaults();
        options.Parse.Flatten = false;
        options.Sampling.SampleReservoir = 500;

        Apply(options, "{}");

        Assert.False(options.Parse.Flatten);
        Assert.Equal(500, options.Sampling.SampleReservoir);
    }

    [Fact]
    public void ApplyRuntime_RuntimeOverridesBuiltInDefault()
    {
        var options = NewDefaults();
        Assert.True(options.Parse.Flatten);

        Apply(options, """{ "flatten": false }""");

        Assert.False(options.Parse.Flatten);
    }

    [Fact]
    public void ApplyRuntime_ExplicitOptionNotOverridden()
    {
        var options = NewDefaults();
        options.Parse.Flatten = false; // 与默认 true 不同 → 显式

        Apply(options, """{ "flatten": true }""");

        Assert.False(options.Parse.Flatten);
    }

    [Fact]
    public void ApplyRuntime_SamplingReservoirAndStep()
    {
        var options = NewDefaults();

        Apply(options, """{ "sampling": { "reservoir": 1000, "step": 5 } }""");

        Assert.Equal(1000, options.Sampling.SampleReservoir);
        Assert.Equal(5, options.Sampling.SampleStep);
    }

    [Fact]
    public void ApplyRuntime_JsonPathRootAndRecord()
    {
        var options = NewDefaults();

        Apply(options, """{ "jsonPath": { "root": "$.data[*]", "record": "$.payload" } }""");

        Assert.Equal("$.data[*]", options.Input.RootPath);
        Assert.Equal("$.payload", options.Input.RecordPath);
    }

    [Fact]
    public void ApplyRuntime_PiiFailOnLevel_OnlyWhenOptionEmpty()
    {
        var options = NewDefaults();
        options.Gates.FailOnPiiLevel = "C";

        Apply(options, """{ "pii": { "failOnLevel": "A" } }""");

        Assert.Equal("C", options.Gates.FailOnPiiLevel);
    }

    [Fact]
    public void ApplyRuntime_DeepAnalysisDepthEnablesFlags()
    {
        var options = NewDefaults();

        Apply(options, """
        {
          "deepAnalysis": {
            "correlation": true,
            "stringPatterns": true,
            "unicodeHygiene": true,
            "timeSeries": true,
            "fuzzyDedupField": "title"
          }
        }
        """);

        Assert.True(options.Advanced.EnableCorrelation);
        Assert.True(options.Advanced.EnableStringPatterns);
        Assert.True(options.Advanced.EnableUnicodeHygiene);
        Assert.True(options.Advanced.EnableTimeSeries);
        Assert.Equal("title", options.Advanced.FuzzyDedupField);
    }

    [Fact]
    public void ApplyRuntime_CheckpointPathAndResumeFrom()
    {
        var options = NewDefaults();

        Apply(options, """
        {
          "checkpoint": {
            "path": "ck.snapshot",
            "resumeFrom": "resume.snapshot",
            "baseline": "baseline.json"
          }
        }
        """);

        Assert.Equal("ck.snapshot", options.Incremental.CheckpointPath);
        Assert.Equal("resume.snapshot", options.Incremental.ResumeFrom);
        Assert.Equal("baseline.json", options.Incremental.BaselinePath);
    }

    [Fact]
    public void ApplyRuntime_MemoryMaxMBAndMode()
    {
        var options = NewDefaults();

        Apply(options, """{ "memory": { "maxMB": 2048, "mode": "hard" } }""");

        Assert.Equal(2048, options.Memory.MaxMemoryMB);
        Assert.Equal(MaxMemoryMode.Hard, options.Memory.MaxMemoryMode);
    }

    [Fact]
    public void ApplyRuntime_MemoryModeInvalid_StaysDefault()
    {
        var options = NewDefaults();

        Apply(options, """{ "memory": { "mode": "panic" } }""");

        Assert.Equal(MaxMemoryMode.Soft, options.Memory.MaxMemoryMode);
    }

    [Fact]
    public void ApplyRuntime_ProgressEnabledFalse_OverridesDefaultTrue()
    {
        var options = NewDefaults();
        Assert.True(options.Progress.ShowProgress);

        Apply(options, """{ "progress": { "enabled": false } }""");

        Assert.False(options.Progress.ShowProgress);
    }

    [Fact]
    public void ApplyRuntime_AuditLogAndOperator()
    {
        var options = NewDefaults();
        Assert.Equal("anonymous", options.Audit.Operator);

        Apply(options, """{ "audit": { "log": "audit.ndjson", "operator": "ci-runner" } }""");

        Assert.Equal("audit.ndjson", options.Audit.AuditLogPath);
        Assert.Equal("ci-runner", options.Audit.Operator);
    }

    [Fact]
    public void ApplyRuntime_OutputsPrimaryAndSchemaCode()
    {
        var options = NewDefaults();

        Apply(options, """
        {
          "outputs": {
            "primary": "report.html",
            "violations": "v.json",
            "quality": "q.json",
            "schemaCode": "csharp:out.cs"
          }
        }
        """);

        Assert.Equal("report.html", options.Report.OutputPath);
        Assert.Equal("v.json", options.Report.ViolationsPath);
        Assert.Equal("q.json", options.Report.QualityPath);
        Assert.Equal("csharp", options.Report.ExportSchema);
        Assert.Equal("out.cs", options.Report.SchemaOutputPath);
    }

    [Fact]
    public void ApplyRuntime_GatesFailOnViolationsAndOutlierRatio()
    {
        var options = NewDefaults();
        Assert.False(options.Gates.FailOnViolations);
        Assert.Null(options.Gates.FailOnOutlierRatio);

        Apply(options, """{ "gates": { "failOnViolations": true, "failOnOutlierRatio": 0.05 } }""");

        Assert.True(options.Gates.FailOnViolations);
        Assert.Equal(0.05, options.Gates.FailOnOutlierRatio);
    }

    [Fact]
    public void ApplyRuntime_SelectedFieldsAndFilter()
    {
        var options = NewDefaults();

        Apply(options, """{ "selectedFields": ["id", "name"], "filter": "age > 0" }""");

        Assert.Equal("id,name", options.FieldSelection.Fields);
        Assert.Equal("age > 0", options.FieldSelection.Filter);
    }

    [Fact]
    public void ApplyRuntime_SelectedFieldsDoesNotOverrideExplicitFields()
    {
        var options = NewDefaults();
        options.FieldSelection.Fields = "explicit_field";

        Apply(options, """{ "selectedFields": ["id", "name"] }""");

        Assert.Equal("explicit_field", options.FieldSelection.Fields);
    }

    [Fact]
    public void ApplyRuntime_FullRuntimePopulatesEverything()
    {
        var options = NewDefaults();

        Apply(options, """
        {
          "flatten": false,
          "csvInferNumbers": false,
          "maxValuesToShow": 20,
          "maxParallelism": 4,
          "allowUnknownRules": true,
          "selectedFields": ["a"],
          "filter": "x > 0",
          "sampling": { "reservoir": 100 },
          "jsonPath": { "root": "$.a" },
          "schema": { "snapshot": "s.json", "allowBreakingChanges": false },
          "distribution": { "snapshot": "d.json" },
          "pii": { "off": true },
          "gates": { "failOnViolations": true, "failOnOutlierRatio": 0.1 },
          "deepAnalysis": { "correlation": true },
          "checkpoint": { "path": "ck.snap" },
          "memory": { "maxMB": 512, "mode": "soft" },
          "progress": { "enabled": false },
          "audit": { "log": "a.ndjson", "operator": "op" },
          "outputs": { "primary": "out.html" }
        }
        """);

        Assert.False(options.Parse.Flatten);
        Assert.False(options.Parse.CsvInferNumbers);
        Assert.Equal(20, options.Parse.MaxValuesToShow);
        Assert.Equal(4, options.Parse.MaxDegreeOfParallelism);
        Assert.True(options.Rules.AllowUnknownRules);
        Assert.Equal("a", options.FieldSelection.Fields);
        Assert.Equal("x > 0", options.FieldSelection.Filter);
        Assert.Equal(100, options.Sampling.SampleReservoir);
        Assert.Equal("$.a", options.Input.RootPath);
        Assert.Equal("s.json", options.Schema.SchemaSnapshotPath);
        Assert.False(options.Gates.AllowBreakingSchemaChanges);
        Assert.Equal("d.json", options.Distribution.DistributionSnapshotPath);
        Assert.True(options.Pii.PiiOff);
        Assert.True(options.Gates.FailOnViolations);
        Assert.Equal(0.1, options.Gates.FailOnOutlierRatio);
        Assert.True(options.Advanced.EnableCorrelation);
        Assert.Equal("ck.snap", options.Incremental.CheckpointPath);
        Assert.Equal(512, options.Memory.MaxMemoryMB);
        Assert.False(options.Progress.ShowProgress);
        Assert.Equal("a.ndjson", options.Audit.AuditLogPath);
        Assert.Equal("op", options.Audit.Operator);
        Assert.Equal("out.html", options.Report.OutputPath);
    }

    [Fact]
    public void ApplyRuntime_ExplicitRuntimeOverBuiltInDefault_PriorityChain()
    {
        var options = new AnalysisOptions
        {
            Input = { InputPath = "x.json" }
        };

        Apply(options, """
        { "flatten": false, "csvInferNumbers": false, "maxValuesToShow": 10 }
        """);

        Assert.False(options.Parse.Flatten);
        Assert.False(options.Parse.CsvInferNumbers);
        Assert.Equal(10, options.Parse.MaxValuesToShow);

        // 显式设值（模拟 CLI 显式参数），应当不丢失
        options.Parse.Flatten = true;
        options.Parse.MaxValuesToShow = 5;

        Assert.True(options.Parse.Flatten);
        Assert.Equal(5, options.Parse.MaxValuesToShow);
        Assert.False(options.Parse.CsvInferNumbers);
    }
}
