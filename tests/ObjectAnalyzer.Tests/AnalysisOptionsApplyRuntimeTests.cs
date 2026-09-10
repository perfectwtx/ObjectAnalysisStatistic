using System.Text.Json;
using System.Text.Json.Nodes;
using ObjectAnalyzer.Analysis;

namespace ObjectAnalyzer.Tests;

/// <summary>
/// RulesToOptionsConverter 的合并语义测试：验证 CLI/API 显式值 > rules.runtime > AnalysisOptions 内置默认的优先级链。
/// </summary>
public class AnalysisOptionsApplyRuntimeTests
{
    private static AnalysisOptions NewDefaults() => new() { Input = { InputPath = "data.json" } };
    private static AnalysisRules ParseRules(string json) => JsonSerializer.Deserialize<AnalysisRules>(json, AnalysisRules.RulesJsonOptions) ?? new AnalysisRules();
    private static void Apply(AnalysisOptions options, string runtimeJson) => RulesToOptionsConverter.Apply(options, new AnalysisRules { Runtime = JsonNode.Parse(runtimeJson) as JsonObject });

    [Fact] public void ApplyRuntime_NullRuntime_NoChange() { var o = NewDefaults(); o.Parse.Flatten = false; o.Sampling.SampleReservoir = 500; RulesToOptionsConverter.Apply(o, null); Assert.False(o.Parse.Flatten); Assert.Equal(500, o.Sampling.SampleReservoir); }
    [Fact] public void ApplyRuntime_EmptyRuntime_NoChange() { var o = NewDefaults(); o.Parse.Flatten = false; o.Sampling.SampleReservoir = 500; Apply(o, "{}"); Assert.False(o.Parse.Flatten); Assert.Equal(500, o.Sampling.SampleReservoir); }
    [Fact] public void ApplyRuntime_RuntimeOverridesBuiltInDefault() { var o = NewDefaults(); Assert.True(o.Parse.Flatten); Apply(o, """{ "flatten": false }"""); Assert.False(o.Parse.Flatten); }
    [Fact] public void ApplyRuntime_ExplicitOptionNotOverridden() { var o = NewDefaults(); o.Parse.Flatten = false; Apply(o, """{ "flatten": true }"""); Assert.False(o.Parse.Flatten); }
    [Fact] public void ApplyRuntime_SamplingReservoirAndStep() { var o = NewDefaults(); Apply(o, """{ "sampling": { "reservoir": 1000, "step": 5 } }"""); Assert.Equal(1000, o.Sampling.SampleReservoir); Assert.Equal(5, o.Sampling.SampleStep); }
    [Fact] public void ApplyRuntime_JsonPathRootAndRecord() { var o = NewDefaults(); Apply(o, """{ "jsonPath": { "root": "$.data[*]", "record": "$.payload" } }"""); Assert.Equal("$.data[*]", o.Input.RootPath); Assert.Equal("$.payload", o.Input.RecordPath); }
    [Fact] public void ApplyRuntime_PiiFailOnLevel_OnlyWhenOptionEmpty() { var o = NewDefaults(); o.Gates.FailOnPiiLevel = "C"; Apply(o, """{ "pii": { "failOnLevel": "A" } }"""); Assert.Equal("C", o.Gates.FailOnPiiLevel); }
    [Fact] public void ApplyRuntime_DeepAnalysisDepthEnablesFlags() { var o = NewDefaults(); Apply(o, """{ "deepAnalysis": { "correlation": true, "stringPatterns": true, "unicodeHygiene": true, "timeSeries": true, "fuzzyDedupField": "title" } }"""); Assert.True(o.Advanced.EnableCorrelation); Assert.True(o.Advanced.EnableStringPatterns); Assert.True(o.Advanced.EnableUnicodeHygiene); Assert.True(o.Advanced.EnableTimeSeries); Assert.Equal("title", o.Advanced.FuzzyDedupField); }
    [Fact] public void ApplyRuntime_CheckpointPathAndResumeFrom() { var o = NewDefaults(); Apply(o, """{ "checkpoint": { "path": "ck.snapshot", "resumeFrom": "resume.snapshot", "baseline": "baseline.json" } }"""); Assert.Equal("ck.snapshot", o.Incremental.CheckpointPath); Assert.Equal("resume.snapshot", o.Incremental.ResumeFrom); Assert.Equal("baseline.json", o.Incremental.BaselinePath); }
    [Fact] public void ApplyRuntime_MemoryMaxMBAndMode() { var o = NewDefaults(); Apply(o, """{ "memory": { "maxMB": 2048, "mode": "hard" } }"""); Assert.Equal(2048, o.Memory.MaxMemoryMB); Assert.Equal(MaxMemoryMode.Hard, o.Memory.MaxMemoryMode); }
    [Fact] public void ApplyRuntime_MemoryModeInvalid_StaysDefault() { var o = NewDefaults(); Apply(o, """{ "memory": { "mode": "panic" } }"""); Assert.Equal(MaxMemoryMode.Soft, o.Memory.MaxMemoryMode); }
    [Fact] public void ApplyRuntime_ProgressEnabledFalse_OverridesDefaultTrue() { var o = NewDefaults(); Assert.True(o.Progress.ShowProgress); Apply(o, """{ "progress": { "enabled": false } }"""); Assert.False(o.Progress.ShowProgress); }
    [Fact] public void ApplyRuntime_AuditLogAndOperator() { var o = NewDefaults(); Assert.Equal("anonymous", o.Audit.Operator); Apply(o, """{ "audit": { "log": "audit.ndjson", "operator": "ci-runner" } }"""); Assert.Equal("audit.ndjson", o.Audit.AuditLogPath); Assert.Equal("ci-runner", o.Audit.Operator); }
    [Fact] public void ApplyRuntime_OutputsPrimaryAndSchemaCode() { var o = NewDefaults(); Apply(o, """{ "outputs": { "primary": "report.html", "violations": "v.json", "quality": "q.json", "schemaCode": "csharp:out.cs" } }"""); Assert.Equal("report.html", o.Report.OutputPath); Assert.Equal("v.json", o.Report.ViolationsPath); Assert.Equal("q.json", o.Report.QualityPath); Assert.Equal("csharp", o.Report.ExportSchema); Assert.Equal("out.cs", o.Report.SchemaOutputPath); }
    [Fact] public void ApplyRuntime_GatesFailOnViolationsAndOutlierRatio() { var o = NewDefaults(); Assert.False(o.Gates.FailOnViolations); Assert.Null(o.Gates.FailOnOutlierRatio); Apply(o, """{ "gates": { "failOnViolations": true, "failOnOutlierRatio": 0.05 } }"""); Assert.True(o.Gates.FailOnViolations); Assert.Equal(0.05, o.Gates.FailOnOutlierRatio); }
    [Fact] public void ApplyRuntime_SelectedFieldsAndFilter() { var o = NewDefaults(); Apply(o, """{ "selectedFields": ["id", "name"], "filter": "age > 0" }"""); Assert.Equal("id,name", o.FieldSelection.Fields); Assert.Equal("age > 0", o.FieldSelection.Filter); }
    [Fact] public void ApplyRuntime_SelectedFieldsDoesNotOverrideExplicitFields() { var o = NewDefaults(); o.FieldSelection.Fields = "explicit_field"; Apply(o, """{ "selectedFields": ["id", "name"] }"""); Assert.Equal("explicit_field", o.FieldSelection.Fields); }
    [Fact] public void ApplyRuntime_FullRuntimePopulatesEverything() { var o = NewDefaults(); Apply(o, """{ "flatten": false, "csvInferNumbers": false, "maxValuesToShow": 20, "maxParallelism": 4, "allowUnknownRules": true, "selectedFields": ["a"], "filter": "x > 0", "sampling": { "reservoir": 100 }, "jsonPath": { "root": "$.a" }, "schema": { "snapshot": "s.json", "allowBreakingChanges": false }, "distribution": { "snapshot": "d.json" }, "pii": { "off": true }, "gates": { "failOnViolations": true, "failOnOutlierRatio": 0.1 }, "deepAnalysis": { "correlation": true }, "checkpoint": { "path": "ck.snap" }, "memory": { "maxMB": 512, "mode": "soft" }, "progress": { "enabled": false }, "audit": { "log": "a.ndjson", "operator": "op" }, "outputs": { "primary": "out.html" } }"""); Assert.False(o.Parse.Flatten); Assert.False(o.Parse.CsvInferNumbers); Assert.Equal(20, o.Parse.MaxValuesToShow); Assert.Equal(4, o.Parse.MaxDegreeOfParallelism); Assert.True(o.Rules.AllowUnknownRules); Assert.Equal("a", o.FieldSelection.Fields); Assert.Equal("x > 0", o.FieldSelection.Filter); Assert.Equal(100, o.Sampling.SampleReservoir); Assert.Equal("$.a", o.Input.RootPath); Assert.Equal("s.json", o.Schema.SchemaSnapshotPath); Assert.False(o.Gates.AllowBreakingSchemaChanges); Assert.Equal("d.json", o.Distribution.DistributionSnapshotPath); Assert.True(o.Pii.PiiOff); Assert.True(o.Gates.FailOnViolations); Assert.Equal(0.1, o.Gates.FailOnOutlierRatio); Assert.True(o.Advanced.EnableCorrelation); Assert.Equal("ck.snap", o.Incremental.CheckpointPath); Assert.Equal(512, o.Memory.MaxMemoryMB); Assert.False(o.Progress.ShowProgress); Assert.Equal("a.ndjson", o.Audit.AuditLogPath); Assert.Equal("op", o.Audit.Operator); Assert.Equal("out.html", o.Report.OutputPath); }
    [Fact] public void ApplyRuntime_ExplicitRuntimeOverBuiltInDefault_PriorityChain() { var o = new AnalysisOptions { Input = { InputPath = "x.json" } }; Apply(o, """{ "flatten": false, "csvInferNumbers": false, "maxValuesToShow": 10 }"""); Assert.False(o.Parse.Flatten); Assert.False(o.Parse.CsvInferNumbers); Assert.Equal(10, o.Parse.MaxValuesToShow); o.Parse.Flatten = true; o.Parse.MaxValuesToShow = 5; Assert.True(o.Parse.Flatten); Assert.Equal(5, o.Parse.MaxValuesToShow); Assert.False(o.Parse.CsvInferNumbers); }

    [Fact]
    public void ApplyRuntime_GatesScoreThresholds()
    {
        var o = NewDefaults();
        Apply(o, """{ "gates": { "failOnOverallScoreBelow": 90, "failOnCompletenessBelow": 95, "failOnValidityBelow": 98, "failOnUniquenessBelow": 95, "failOnConsistencyBelow": 95, "failOnAnomalyControlBelow": 95 } }""");
        Assert.Equal(90, o.Gates.FailOnOverallScoreBelow); Assert.Equal(95, o.Gates.FailOnCompletenessBelow); Assert.Equal(98, o.Gates.FailOnValidityBelow); Assert.Equal(95, o.Gates.FailOnUniquenessBelow); Assert.Equal(95, o.Gates.FailOnConsistencyBelow); Assert.Equal(95, o.Gates.FailOnAnomalyControlBelow); Assert.True(o.Gates.HasScoreThresholds);
    }

    [Fact]
    public void ApplyRuntime_ScoreThresholdsDoNotOverrideExplicitValues()
    {
        var o = NewDefaults(); o.Gates.FailOnOverallScoreBelow = 80;
        Apply(o, """{ "gates": { "failOnOverallScoreBelow": 90 } }""");
        Assert.Equal(80, o.Gates.FailOnOverallScoreBelow);
    }
}
