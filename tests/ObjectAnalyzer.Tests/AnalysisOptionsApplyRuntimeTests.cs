using ObjectAnalyzer.Analysis;

namespace ObjectAnalyzer.Tests;

/// <summary>
/// AnalysisOptionsExtensions.ApplyRuntime 的合并语义测试：
/// 验证 CLI/API 显式值 &gt; rules.runtime &gt; AnalysisOptions 内置默认的优先级链。
/// </summary>
public class AnalysisOptionsApplyRuntimeTests
{
    private static AnalysisOptions NewDefaults() => new()
    {
        InputPath = "data.json",
    };

    [Fact]
    public void ApplyRuntime_NullRuntime_NoChange()
    {
        var options = NewDefaults();
        options.Flatten = false;
        options.SampleReservoir = 500;

        options.ApplyRuntime(null);

        Assert.False(options.Flatten); // 显式值未被改
        Assert.Equal(500, options.SampleReservoir);
    }

    [Fact]
    public void ApplyRuntime_AllNullRuntimeProperties_NoChange()
    {
        // 全 null 的 runtime 节点 → 全部字段都是"未指定"，不动 options
        var options = NewDefaults();
        options.Flatten = false;
        options.SampleReservoir = 500;

        options.ApplyRuntime(new AnalysisRuntime());

        Assert.False(options.Flatten);
        Assert.Equal(500, options.SampleReservoir);
    }

    [Fact]
    public void ApplyRuntime_RuntimeOverridesBuiltInDefault()
    {
        // options 是默认（Flatten=true），runtime.flatten=false 应生效
        var options = NewDefaults();
        Assert.True(options.Flatten);

        options.ApplyRuntime(new AnalysisRuntime { Flatten = false });

        Assert.False(options.Flatten);
    }

    [Fact]
    public void ApplyRuntime_ExplicitOptionNotOverridden()
    {
        // options.Flatten=false 是显式设的（与默认 true 不同），runtime.flatten=true 不能覆盖
        var options = NewDefaults();
        options.Flatten = false;

        options.ApplyRuntime(new AnalysisRuntime { Flatten = true });

        Assert.False(options.Flatten); // 仍为显式值
    }

    [Fact]
    public void ApplyRuntime_SamplingReservoirAndStep()
    {
        var options = NewDefaults();

        options.ApplyRuntime(new AnalysisRuntime
        {
            Sampling = new SamplingConfig { Reservoir = 1000, Step = 5 }
        });

        Assert.Equal(1000, options.SampleReservoir);
        Assert.Equal(5, options.SampleStep);
    }

    [Fact]
    public void ApplyRuntime_JsonPathRootAndRecord()
    {
        var options = NewDefaults();

        options.ApplyRuntime(new AnalysisRuntime
        {
            JsonPath = new JsonPathConfig { Root = "$.data[*]", Record = "$.payload" }
        });

        Assert.Equal("$.data[*]", options.RootPath);
        Assert.Equal("$.payload", options.RecordPath);
    }

    [Fact]
    public void ApplyRuntime_PiiFailOnLevel_OnlyWhenOptionEmpty()
    {
        // 选项已有 failOnPiiLevel → runtime.failOnLevel 不覆盖
        var options = NewDefaults();
        options.FailOnPiiLevel = "C";

        options.ApplyRuntime(new AnalysisRuntime
        {
            Pii = new PiiRuntimeConfig { FailOnLevel = "A" }
        });

        Assert.Equal("C", options.FailOnPiiLevel);
    }

    [Fact]
    public void ApplyRuntime_P4DepthEnablesP4Flags()
    {
        var options = NewDefaults();

        options.ApplyRuntime(new AnalysisRuntime
        {
            P4 = new P4Config
            {
                Correlation = true,
                StringPatterns = true,
                UnicodeHygiene = true,
                TimeSeries = true,
                FuzzyDedupField = "title"
            }
        });

        Assert.True(options.EnableCorrelation);
        Assert.True(options.EnableStringPatterns);
        Assert.True(options.EnableUnicodeHygiene);
        Assert.True(options.EnableTimeSeries);
        Assert.Equal("title", options.FuzzyDedupField);
    }

    [Fact]
    public void ApplyRuntime_CheckpointPathAndResumeFrom()
    {
        var options = NewDefaults();

        options.ApplyRuntime(new AnalysisRuntime
        {
            Checkpoint = new CheckpointConfig
            {
                Path = "ck.snapshot",
                ResumeFrom = "resume.snapshot",
                Baseline = "baseline.json"
            }
        });

        Assert.Equal("ck.snapshot", options.CheckpointPath);
        Assert.Equal("resume.snapshot", options.ResumeFrom);
        Assert.Equal("baseline.json", options.BaselinePath);
    }

    [Fact]
    public void ApplyRuntime_MemoryMaxMBAndMode()
    {
        var options = NewDefaults();

        options.ApplyRuntime(new AnalysisRuntime
        {
            Memory = new MemoryConfig { MaxMB = 2048, Mode = "hard" }
        });

        Assert.Equal(2048, options.MaxMemoryMB);
        Assert.Equal(MaxMemoryMode.Hard, options.MaxMemoryMode);
    }

    [Fact]
    public void ApplyRuntime_MemoryModeInvalid_StaysDefault()
    {
        var options = NewDefaults();

        options.ApplyRuntime(new AnalysisRuntime
        {
            Memory = new MemoryConfig { Mode = "panic" } // 不是合法枚举
        });

        Assert.Equal(MaxMemoryMode.Soft, options.MaxMemoryMode); // 不动
    }

    [Fact]
    public void ApplyRuntime_ProgressEnabledFalse_OverridesDefaultTrue()
    {
        var options = NewDefaults();
        Assert.True(options.ShowProgress);

        options.ApplyRuntime(new AnalysisRuntime
        {
            Progress = new ProgressConfig { Enabled = false }
        });

        Assert.False(options.ShowProgress);
    }

    [Fact]
    public void ApplyRuntime_AuditLogAndOperator()
    {
        var options = NewDefaults();
        Assert.Equal("anonymous", options.Operator);

        options.ApplyRuntime(new AnalysisRuntime
        {
            Audit = new AuditConfig { Log = "audit.ndjson", Operator = "ci-runner" }
        });

        Assert.Equal("audit.ndjson", options.AuditLogPath);
        Assert.Equal("ci-runner", options.Operator);
    }

    [Fact]
    public void ApplyRuntime_OutputsPrimaryAndSchemaCode()
    {
        var options = NewDefaults();

        options.ApplyRuntime(new AnalysisRuntime
        {
            Outputs = new OutputsConfig
            {
                Primary = "report.html",
                Violations = "v.json",
                Quality = "q.json",
                SchemaCode = "csharp:out.cs"
            }
        });

        Assert.Equal("report.html", options.OutputPath);
        Assert.Equal("v.json", options.ViolationsPath);
        Assert.Equal("q.json", options.QualityPath);
        Assert.Equal("csharp", options.ExportSchema);
        Assert.Equal("out.cs", options.SchemaOutputPath);
    }

    [Fact]
    public void ApplyRuntime_GatesFailOnViolationsAndOutlierRatio()
    {
        var options = NewDefaults();
        Assert.False(options.FailOnViolations);
        Assert.Null(options.FailOnOutlierRatio);

        options.ApplyRuntime(new AnalysisRuntime
        {
            Gates = new GatesConfig
            {
                FailOnViolations = true,
                FailOnOutlierRatio = 0.05
            }
        });

        Assert.True(options.FailOnViolations);
        Assert.Equal(0.05, options.FailOnOutlierRatio);
    }

    [Fact]
    public void ApplyRuntime_SelectedFieldsAndFilter()
    {
        var options = NewDefaults();

        options.ApplyRuntime(new AnalysisRuntime
        {
            SelectedFields = new List<string> { "id", "name" },
            Filter = "age > 0"
        });

        Assert.Equal("id,name", options.Fields);
        Assert.Equal("age > 0", options.Filter);
    }

    [Fact]
    public void ApplyRuntime_SelectedFieldsDoesNotOverrideExplicitFields()
    {
        var options = NewDefaults();
        options.Fields = "explicit_field";

        options.ApplyRuntime(new AnalysisRuntime
        {
            SelectedFields = new List<string> { "id", "name" }
        });

        Assert.Equal("explicit_field", options.Fields);
    }

    [Fact]
    public void ApplyRuntime_FullRuntimePopulatesEverything()
    {
        // 一次性灌满所有节点，验证没有遗漏字段
        var options = NewDefaults();

        options.ApplyRuntime(new AnalysisRuntime
        {
            Flatten = false,
            CsvInferNumbers = false,
            MaxValuesToShow = 20,
            MaxParallelism = 4,
            AllowUnknownRules = true,
            SelectedFields = new List<string> { "a" },
            Filter = "x > 0",
            Sampling = new SamplingConfig { Reservoir = 100 },
            JsonPath = new JsonPathConfig { Root = "$.a" },
            Schema = new SchemaConfig { Snapshot = "s.json", AllowBreakingChanges = false },
            Distribution = new DistributionConfig { Snapshot = "d.json" },
            Pii = new PiiRuntimeConfig { Off = true },
            Gates = new GatesConfig { FailOnViolations = true, FailOnOutlierRatio = 0.1 },
            P4 = new P4Config { Correlation = true },
            Checkpoint = new CheckpointConfig { Path = "ck.snap" },
            Memory = new MemoryConfig { MaxMB = 512, Mode = "soft" },
            Progress = new ProgressConfig { Enabled = false },
            Audit = new AuditConfig { Log = "a.ndjson", Operator = "op" },
            Outputs = new OutputsConfig { Primary = "out.html" }
        });

        Assert.False(options.Flatten);
        Assert.False(options.CsvInferNumbers);
        Assert.Equal(20, options.MaxValuesToShow);
        Assert.Equal(4, options.MaxDegreeOfParallelism);
        Assert.True(options.AllowUnknownRules);
        Assert.Equal("a", options.Fields);
        Assert.Equal("x > 0", options.Filter);
        Assert.Equal(100, options.SampleReservoir);
        Assert.Equal("$.a", options.RootPath);
        Assert.Equal("s.json", options.SchemaSnapshotPath);
        Assert.False(options.AllowBreakingSchemaChanges);
        Assert.Equal("d.json", options.DistributionSnapshotPath);
        Assert.True(options.PiiOff);
        Assert.True(options.FailOnViolations);
        Assert.Equal(0.1, options.FailOnOutlierRatio);
        Assert.True(options.EnableCorrelation);
        Assert.Equal("ck.snap", options.CheckpointPath);
        Assert.Equal(512, options.MaxMemoryMB);
        Assert.False(options.ShowProgress);
        Assert.Equal("a.ndjson", options.AuditLogPath);
        Assert.Equal("op", options.Operator);
        Assert.Equal("out.html", options.OutputPath);
    }

    [Fact]
    public void ApplyRuntime_ExplicitRuntimeOverBuiltInDefault_PriorityChain()
    {
        // 完整优先级链验证：explicit > runtime > default
        var options = new AnalysisOptions
        {
            InputPath = "x.json",
            // 全部保持默认
        };

        // 第一步：runtime 应用
        options.ApplyRuntime(new AnalysisRuntime
        {
            Flatten = false,
            CsvInferNumbers = false,
            MaxValuesToShow = 10
        });

        Assert.False(options.Flatten);
        Assert.False(options.CsvInferNumbers);
        Assert.Equal(10, options.MaxValuesToShow);

        // 第二步：调用方显式设值（模拟 CLI 显式参数）
        options.Flatten = true; // 显式 = true，应该能覆盖 runtime=false
        options.MaxValuesToShow = 5;

        Assert.True(options.Flatten);
        Assert.Equal(5, options.MaxValuesToShow);
        Assert.False(options.CsvInferNumbers); // runtime 仍然生效（无显式覆盖）
    }
}
