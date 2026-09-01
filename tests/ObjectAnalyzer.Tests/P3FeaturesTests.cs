namespace ObjectAnalyzer.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ObjectAnalyzer.Analysis;

/// <summary>
/// P3 高级分析能力测试：日期/时间深度剖析、异常值检测（IQR）、Schema 快照 + Diff 集成。
/// </summary>
[Collection(AnalysisRunnerSequentialGroup.Name)]
public class P3FeaturesTests
{
    // ──────────────────────────────────────────────
    // 日期/时间深度剖析
    // ──────────────────────────────────────────────

    [Fact]
    public void DateTime_Field_Populates_MinMax_AndHistogram()
    {
        var objects = new List<Dictionary<string, object?>>
        {
            new() { ["ts"] = "2025-01-15T10:30:00Z" },
            new() { ["ts"] = "2025-01-16T11:00:00Z" },
            new() { ["ts"] = "2025-01-17T09:15:00Z" },
            new() { ["ts"] = "2025-01-18T14:20:00Z" }
        };

        var result = Analyze(objects);
        var stat = result.FieldStatistics.Single(s => s.FieldName == "ts");

        Assert.Equal("DateTime", stat.SemanticType);
        Assert.Equal(4, stat.DateTimeCount);
        Assert.NotNull(stat.MinDateTime);
        Assert.NotNull(stat.MaxDateTime);
        Assert.NotNull(stat.DateHistogram);
        Assert.NotEmpty(stat.DateHistogram!);
        Assert.Equal(stat.DateTimeCount, stat.DateHistogram!.Sum(b => b.Count));
    }

    [Fact]
    public void DateTime_LocalTimezoneOffset_IsConvertedToUtc()
    {
        var objects = new List<Dictionary<string, object?>>
        {
            new() { ["ts"] = "2025-06-15T10:00:00+08:00" },  // = 02:00:00 UTC
            new() { ["ts"] = "2025-06-15T12:00:00+08:00" }   // = 04:00:00 UTC
        };

        var result = Analyze(objects);
        var stat = result.FieldStatistics.Single(s => s.FieldName == "ts");

        Assert.NotNull(stat.MinDateTime);
        Assert.Equal(2, stat.MinDateTime!.Value.UtcDateTime.Hour);
        Assert.Equal(4, stat.MaxDateTime!.Value.UtcDateTime.Hour);
    }

    [Fact]
    public void DateTime_NaiveString_TreatedAsUtc()
    {
        var objects = new List<Dictionary<string, object?>>
        {
            new() { ["ts"] = "2025-01-15T10:30:00" }  // 无时区 → UTC
        };

        var result = Analyze(objects);
        var stat = result.FieldStatistics.Single(s => s.FieldName == "ts");

        Assert.NotNull(stat.MinDateTime);
        Assert.Equal(TimeSpan.Zero, stat.MinDateTime!.Value.Offset);
    }

    [Fact]
    public void DateTime_HistogramBucket_HasAdaptiveLabel()
    {
        var bucket = new DateHistogramBucket
        {
            From = new DateTimeOffset(2025, 1, 15, 10, 0, 0, TimeSpan.Zero),
            To = new DateTimeOffset(2025, 1, 15, 10, 30, 0, TimeSpan.Zero),
            Count = 5
        };
        // 30 分钟桶应输出分级标签
        var label = bucket.ToString();
        Assert.Contains("10:00", label);
        Assert.Contains("10:30", label);
    }

    // ──────────────────────────────────────────────
    // 异常值检测（IQR 1.5×）
    // ──────────────────────────────────────────────

    [Fact]
    public void Outlier_ClearlySeparatedValues_AreFlagged()
    {
        // 9 个值聚在 48-53 之间，2 个明显离群：1000 / -800
        var objects = new List<Dictionary<string, object?>>
        {
            new() { ["score"] = 50.0 }, new() { ["score"] = 52.0 }, new() { ["score"] = 48.0 },
            new() { ["score"] = 51.0 }, new() { ["score"] = 49.0 }, new() { ["score"] = 53.0 },
            new() { ["score"] = 50.0 }, new() { ["score"] = 51.0 }, new() { ["score"] = 49.0 },
            new() { ["score"] = 1000.0 }, new() { ["score"] = -800.0 }
        };

        var result = Analyze(objects);
        var stat = result.FieldStatistics.Single(s => s.FieldName == "score");

        Assert.Equal("iqr", stat.OutlierMethod);
        Assert.NotNull(stat.OutlierCount);
        Assert.True(stat.OutlierCount! > 0, "明显的离群点应被 IQR 检测到");
        Assert.NotNull(stat.OutlierBounds);
        Assert.NotEmpty(stat.OutlierBounds!);
    }

    [Fact]
    public void Outlier_TightDistribution_FlagsNothing()
    {
        // 10 个完全相同的值 → 0% 异常
        var objects = Enumerable.Range(0, 10)
            .Select(i => new Dictionary<string, object?> { ["score"] = 50.0 })
            .ToList();

        var result = Analyze(objects);
        var stat = result.FieldStatistics.Single(s => s.FieldName == "score");

        // 完全相同分布 → 直方图只有一个桶；IQR=0 → 所有值都"在区间内"
        // 实现按直方图近似归类：当所有桶都在 [lo, hi] 内时，outlierCount=0
        Assert.Equal("iqr", stat.OutlierMethod);
        Assert.NotNull(stat.OutlierCount);
        Assert.Equal(0, stat.OutlierCount);
    }

    [Fact]
    public void Outlier_TooFewSamples_NotComputed()
    {
        // < 4 个数值样本时不计算（避免小样本抖动）
        var objects = new List<Dictionary<string, object?>>
        {
            new() { ["score"] = 50.0 },
            new() { ["score"] = 51.0 }
        };

        var result = Analyze(objects);
        var stat = result.FieldStatistics.Single(s => s.FieldName == "score");

        Assert.Null(stat.OutlierMethod);
        Assert.Null(stat.OutlierCount);
        Assert.Null(stat.OutlierRatio);
    }

    [Fact]
    public void Outlier_QualityCheck_MaxOutlierRatio_TriggersViolation()
    {
        // 9 个正常 + 2 个离群 = 18% 异常值；阈值设为 0.10 应触发
        var objects = new List<Dictionary<string, object?>>
        {
            new() { ["score"] = 50.0 }, new() { ["score"] = 52.0 }, new() { ["score"] = 48.0 },
            new() { ["score"] = 51.0 }, new() { ["score"] = 49.0 }, new() { ["score"] = 53.0 },
            new() { ["score"] = 50.0 }, new() { ["score"] = 51.0 }, new() { ["score"] = 49.0 },
            new() { ["score"] = 1000.0 }, new() { ["score"] = -800.0 }
        };
        var rules = new AnalysisRules();
        rules.Fields["score"] = new FieldRule { MaxOutlierRatio = 0.10 };

        var result = Analyze(objects, rules);
        var violation = result.QualityViolations.SingleOrDefault(v => v.Check == QualityCheckTypes.OutlierRatio);
        Assert.NotNull(violation);
        Assert.True(violation!.Approximate, "对数直方图近似归类应标记为近似");
    }

    // ──────────────────────────────────────────────
    // Schema 快照 + Diff 集成（AnalysisRunner 级别）
    // ──────────────────────────────────────────────

    [Fact]
    public void AnalysisRunner_DiffSchema_AgainstIdenticalSnapshot_NoBreakingChanges()
    {
        // 准备数据：两次分析使用同一份输入，第二次拿第一次的快照当 baseline
        string inputContent = "[{\"id\":1,\"name\":\"Alice\"},{\"id\":2,\"name\":\"Bob\"}]";
        string inputPath = Path.GetTempFileName();
        File.WriteAllText(inputPath, inputContent);

        string snapshotPath = Path.Combine(Path.GetTempPath(), $"snap-{Guid.NewGuid():N}.json");

        try
        {
            var options = new AnalysisOptions
            {
                Input = { InputPath = inputPath },
                Schema = { SchemaSnapshotPath = snapshotPath },
                Gates = { AllowBreakingSchemaChanges = true }
            };

            int code = AnalysisRunner.Run(options);

            Assert.Equal(0, code);
            Assert.True(File.Exists(snapshotPath), "快照应已导出");

            // 第二次跑：拿第一次的快照当 baseline
            var options2 = new AnalysisOptions
            {
                Input = { InputPath = inputPath },
                Schema = { DiffSchemaBaseline = snapshotPath },
                Gates = { AllowBreakingSchemaChanges = false }  // 严格模式：含破坏性变更则退出码 6
            };

            int code2 = AnalysisRunner.Run(options2);

            // 同一份数据两次分析结果应完全一致，无破坏性变更
            Assert.Equal(0, code2);
        }
        finally
        {
            File.Delete(inputPath);
            if (File.Exists(snapshotPath)) File.Delete(snapshotPath);
        }
    }

    [Fact]
    public void AnalysisRunner_DiffSchema_Respects_AllowBreakingSchemaChanges_False()
    {
        // baseline: 完整 4 个字段 → 先生成 baseline 快照
        string baselineInputPath = Path.GetTempFileName();
        File.WriteAllText(baselineInputPath,
            "[{\"id\":1,\"name\":\"Alice\",\"email\":\"a@x.com\",\"createdAt\":\"2025-01-01T00:00:00Z\"}," +
            "{\"id\":2,\"name\":\"Bob\",\"email\":\"b@x.com\",\"createdAt\":\"2025-01-02T00:00:00Z\"}]");

        // 第一次跑：导出 baseline schema snapshot
        string baselineSnapshotPath = Path.Combine(Path.GetTempPath(), $"baseline-snap-{Guid.NewGuid():N}.json");

        // current: 删除了 email 和 createdAt → 破坏性变更
        string inputPath = Path.GetTempFileName();
        File.WriteAllText(inputPath,
            "[{\"id\":1,\"name\":\"Alice\"},{\"id\":2,\"name\":\"Bob\"}]");

        try
        {
            var baselineOptions = new AnalysisOptions
            {
                Input = { InputPath = baselineInputPath },
                Schema = { SchemaSnapshotPath = baselineSnapshotPath }
            };
            Assert.Equal(0, AnalysisRunner.Run(baselineOptions));
            Assert.True(File.Exists(baselineSnapshotPath));

            // 第二次跑：拿 baseline 快照做 diff
            var options = new AnalysisOptions
            {
                Input = { InputPath = inputPath },
                Schema = { DiffSchemaBaseline = baselineSnapshotPath },
                Gates = { AllowBreakingSchemaChanges = false }  // 严格模式
            };

            int code = AnalysisRunner.Run(options);
            Assert.Equal(6, code);  // 破坏性变更退出码 6
        }
        finally
        {
            File.Delete(baselineInputPath);
            File.Delete(inputPath);
            if (File.Exists(baselineSnapshotPath)) File.Delete(baselineSnapshotPath);
        }
    }

    [Fact]
    public void AnalysisRunner_DiffSchema_MissingBaselineFile_ExitsWith1()
    {
        string inputPath = Path.GetTempFileName();
        File.WriteAllText(inputPath, "[{\"id\":1}]");

        try
        {
            var options = new AnalysisOptions
            {
                Input = { InputPath = inputPath },
                Schema = { DiffSchemaBaseline = Path.Combine(Path.GetTempPath(),
                    $"non-existent-{Guid.NewGuid():N}.json") }
            };

            int code = AnalysisRunner.Run(options);
            Assert.Equal(1, code);  // 文件不存在
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Fact]
    public void AnalysisRunner_ExportSchemaSnapshot_CreatesFile()
    {
        string inputPath = Path.GetTempFileName();
        File.WriteAllText(inputPath, "[{\"id\":1,\"name\":\"Alice\"},{\"id\":2,\"name\":\"Bob\"}]");
        string snapshotPath = Path.Combine(Path.GetTempPath(), $"snap-{Guid.NewGuid():N}.json");

        try
        {
            var options = new AnalysisOptions
            {
                Input = { InputPath = inputPath },
                Schema = { SchemaSnapshotPath = snapshotPath }
            };

            int code = AnalysisRunner.Run(options);
            Assert.Equal(0, code);
            Assert.True(File.Exists(snapshotPath));

            // 校验 JSON 内容
            var snapshot = SchemaDiffer.LoadFromFile(snapshotPath);
            Assert.True(snapshot.Fields.Count >= 2);
            Assert.Contains(snapshot.Fields, f => f.Path == "id");
            Assert.Contains(snapshot.Fields, f => f.Path == "name");
        }
        finally
        {
            File.Delete(inputPath);
            if (File.Exists(snapshotPath)) File.Delete(snapshotPath);
        }
    }
}
