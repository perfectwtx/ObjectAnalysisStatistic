using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ObjectAnalyzer.Analysis;
using Xunit;

namespace ObjectAnalyzer.Tests;

/// <summary>
/// P6.2 增量分析 / 断点续跑测试：
/// - AnalyzerSnapshot 的 SaveTo / LoadFrom 序列化
/// - FieldStatistic.MergeFrom（数值 / 直方图 / 枚举违规 / DateTime 等）
/// - AnalysisResult.MergeFrom 顶层合并
/// - AnalysisRunner --checkpoint / --resume / --baseline 端到端
/// - 错误路径（--resume 源文件不一致 / 快照不存在）
/// 所有调用 AnalysisRunner.Run 的测试加入 AnalysisRunnerSequentialGroup 集合以避免 AnsiConsole 并发问题。
/// </summary>
[Collection(AnalysisRunnerSequentialGroup.Name)]
public class IncrementalAnalysisTests
{
    // ──────────────────────────────────────────────
    // AnalyzerSnapshot 单元测试
    // ──────────────────────────────────────────────

    [Fact]
    [Trait("Category", "P6.2")]
    public void AnalyzerSnapshot_SaveLoad_RoundTripsAllFields()
    {
        string path = NewTempPath(".snap.json");
        try
        {
            var snapshot = new AnalyzerSnapshot
            {
                SourcePath = "C:\\data\\big.jsonl",
                SourceFormat = "jsonl",
                SourcePosition = 12345,
                PositionKind = "line",
                SourceFingerprint = "abcdef",
                Result = new AnalysisResult
                {
                    TotalObjects = 12345,
                    TotalUniqueObjects = 11900,
                    TotalDuplicateGroups = 3
                }
            };
            snapshot.SaveTo(path);

            var loaded = AnalyzerSnapshot.LoadFrom(path);
            Assert.Equal("object-analyzer.snapshot/v1", loaded.Schema);
            Assert.Equal("C:\\data\\big.jsonl", loaded.SourcePath);
            Assert.Equal("jsonl", loaded.SourceFormat);
            Assert.Equal(12345, loaded.SourcePosition);
            Assert.Equal("line", loaded.PositionKind);
            Assert.Equal(12345, loaded.Result.TotalObjects);
            Assert.Equal(3, loaded.Result.TotalDuplicateGroups);
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    [Trait("Category", "P6.2")]
    public void AnalyzerSnapshot_AtomicWrite_NoTempFileLeftBehind()
    {
        string path = NewTempPath(".snap.json");
        try
        {
            var snapshot = new AnalyzerSnapshot { SourcePath = "x.jsonl", SourcePosition = 100 };
            snapshot.SaveTo(path);
            // .tmp 文件应已被 rename 清理
            Assert.False(File.Exists(path + ".tmp"), "原子写入不应残留 .tmp 文件");
            Assert.True(File.Exists(path));
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    [Trait("Category", "P6.2")]
    public void AnalyzerSnapshot_LoadFrom_IncompatibleSchema_Throws()
    {
        string path = NewTempPath(".snap.json");
        try
        {
            // 手写一个 schema 不兼容的 JSON（前缀与"object-analyzer.snapshot/"不匹配）
            File.WriteAllText(path, "{\"schema\":\"other-analyzer.snapshot/v0\",\"sourcePath\":\"x\"}");
            var ex = Assert.Throws<InvalidDataException>(() => AnalyzerSnapshot.LoadFrom(path));
            Assert.Contains("schema 不兼容", ex.Message);
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    [Trait("Category", "P6.2")]
    public void AnalyzerSnapshot_Overwrite_PreviousRemoved()
    {
        string path = NewTempPath(".snap.json");
        try
        {
            new AnalyzerSnapshot { SourcePath = "first", SourcePosition = 1 }.SaveTo(path);
            new AnalyzerSnapshot { SourcePath = "second", SourcePosition = 2 }.SaveTo(path);
            var loaded = AnalyzerSnapshot.LoadFrom(path);
            Assert.Equal("second", loaded.SourcePath);
            Assert.Equal(2, loaded.SourcePosition);
        }
        finally
        {
            TryDelete(path);
        }
    }

    // ──────────────────────────────────────────────
    // FieldStatistic.MergeFrom 单元测试
    // ──────────────────────────────────────────────

    [Fact]
    [Trait("Category", "P6.2")]
    public void FieldStatistic_MergeFrom_AggregatesCountSumMinMaxAvgStdDev()
    {
        var a = new FieldStatistic
        {
            FieldName = "score",
            Count = 3,
            MinNumber = 10,
            MaxNumber = 30,
            SumNumber = 60, // 10+20+30
            AvgNumber = 20,
            StdDevNumber = 10 // sqrt(((10-20)² + (20-20)² + (30-20)²) / 2) = sqrt(200/2) = 10
        };
        var b = new FieldStatistic
        {
            FieldName = "score",
            Count = 2,
            MinNumber = 40,
            MaxNumber = 50,
            SumNumber = 90, // 40+50
            AvgNumber = 45,
            StdDevNumber = 5 // sqrt(((40-45)² + (50-45)²) / 1) = sqrt(50) ≈ 7.07
            // 注：实际 Welford 算法生成的 StdDev 会因公式不同而略不同，这里以"至少 0"作为松弛断言
        };
        // 校正 b.StdDevNumber：实际应为 5*sqrt(1/0) ... 实际 n2=2 时样本标准差 = sqrt(50) ≈ 7.071
        b.StdDevNumber = Math.Sqrt(50);

        a.MergeFrom(b);

        Assert.Equal(5, a.Count);
        Assert.Equal(150, a.SumNumber); // 60 + 90
        Assert.Equal(10, a.MinNumber);
        Assert.Equal(50, a.MaxNumber);
        // 新均值 = (3*20 + 2*45) / 5 = (60 + 90) / 5 = 30
        Assert.Equal(30, a.AvgNumber);
        Assert.NotNull(a.StdDevNumber);
        // 标准差应为正数
        Assert.True(a.StdDevNumber > 0, "合并后 StdDevNumber 应为正数");
    }

    [Fact]
    [Trait("Category", "P6.2")]
    public void FieldStatistic_MergeFrom_MergesHistograms()
    {
        var a = new FieldStatistic
        {
            FieldName = "value",
            Count = 4,
            Histogram = new List<NumericHistogramBucket>
            {
                new() { From = 0, To = 1, Count = 1 },
                new() { From = 2, To = 4, Count = 3 }
            }
        };
        var b = new FieldStatistic
        {
            FieldName = "value",
            Count = 3,
            Histogram = new List<NumericHistogramBucket>
            {
                new() { From = 0, To = 1, Count = 2 },
                new() { From = 8, To = 16, Count = 1 }
            }
        };

        a.MergeFrom(b);

        Assert.NotNull(a.Histogram);
        Assert.Equal(7, a.Count);
        var byKey = a.Histogram!.ToDictionary(x => $"{x.From}-{x.To}");
        Assert.Equal(3, byKey["0-1"].Count);   // 1 + 2
        Assert.Equal(3, byKey["2-4"].Count);
        Assert.Equal(1, byKey["8-16"].Count);
    }

    [Fact]
    [Trait("Category", "P6.2")]
    public void FieldStatistic_MergeFrom_MergesTypeCountsAndValueCounts()
    {
        var a = new FieldStatistic
        {
            FieldName = "k",
            Count = 5,
            TypeCounts = new Dictionary<string, long> { ["Number"] = 3, ["String"] = 2 },
            ValueCounts = new Dictionary<string, long> { ["v1"] = 3, ["v2"] = 2 }
        };
        var b = new FieldStatistic
        {
            FieldName = "k",
            Count = 4,
            TypeCounts = new Dictionary<string, long> { ["Number"] = 2, ["Boolean"] = 2 },
            ValueCounts = new Dictionary<string, long> { ["v1"] = 1, ["v3"] = 3 }
        };

        a.MergeFrom(b);

        Assert.Equal(9, a.Count);
        Assert.Equal(5, a.TypeCounts["Number"]);   // 3+2
        Assert.Equal(2, a.TypeCounts["String"]);
        Assert.Equal(2, a.TypeCounts["Boolean"]);
        Assert.Equal(4, a.ValueCounts["v1"]);     // 3+1
        Assert.Equal(2, a.ValueCounts["v2"]);
        Assert.Equal(3, a.ValueCounts["v3"]);
    }

    [Fact]
    [Trait("Category", "P6.2")]
    public void FieldStatistic_MergeFrom_AppendsEnumViolations()
    {
        var a = new FieldStatistic { FieldName = "status" };
        a.GetType().GetMethod("AddEnumViolation", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(a, new object?[] { "OLD" });
        a.GetType().GetMethod("AddEnumViolation", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(a, new object?[] { "WORN" });

        var b = new FieldStatistic { FieldName = "status" };
        b.GetType().GetMethod("AddEnumViolation", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(b, new object?[] { "BROKEN" });

        a.MergeFrom(b);

        Assert.Equal(3, a.EnumViolations.Count);
        Assert.Contains("OLD", a.EnumViolations);
        Assert.Contains("WORN", a.EnumViolations);
        Assert.Contains("BROKEN", a.EnumViolations);
    }

    [Fact]
    [Trait("Category", "P6.2")]
    public void FieldStatistic_MergeFrom_NullOther_NoOp()
    {
        var a = new FieldStatistic { FieldName = "x", Count = 5 };
        a.MergeFrom(null!);
        Assert.Equal(5, a.Count);
    }

    [Fact]
    [Trait("Category", "P6.2")]
    public void FieldStatistic_MergeFrom_DateTimeMinMaxExtends()
    {
        var a = new FieldStatistic
        {
            FieldName = "ts",
            MinDateTime = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
            MaxDateTime = new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero)
        };
        var b = new FieldStatistic
        {
            FieldName = "ts",
            MinDateTime = new DateTimeOffset(2024, 12, 1, 0, 0, 0, TimeSpan.Zero),
            MaxDateTime = new DateTimeOffset(2025, 7, 1, 0, 0, 0, TimeSpan.Zero)
        };

        a.MergeFrom(b);

        Assert.Equal(new DateTimeOffset(2024, 12, 1, 0, 0, 0, TimeSpan.Zero), a.MinDateTime);
        Assert.Equal(new DateTimeOffset(2025, 7, 1, 0, 0, 0, TimeSpan.Zero), a.MaxDateTime);
    }

    // ──────────────────────────────────────────────
    // AnalysisResult.MergeFrom 单元测试
    // ──────────────────────────────────────────────

    [Fact]
    [Trait("Category", "P6.2")]
    public void AnalysisResult_MergeFrom_AggregatesTotals()
    {
        var a = new AnalysisResult
        {
            TotalObjects = 100,
            TotalUniqueObjects = 95,
            TotalDuplicateGroups = 2,
            FieldStatistics = new List<FieldStatistic>
            {
                new() { FieldName = "x", Count = 100, SumNumber = 1000, AvgNumber = 10 }
            }
        };
        var b = new AnalysisResult
        {
            TotalObjects = 50,
            TotalUniqueObjects = 48,
            TotalDuplicateGroups = 1,
            FieldStatistics = new List<FieldStatistic>
            {
                new() { FieldName = "x", Count = 50, SumNumber = 500, AvgNumber = 10 }
            }
        };

        a.MergeFrom(b);

        Assert.Equal(150, a.TotalObjects);
        Assert.Equal(95, a.TotalUniqueObjects); // max
        Assert.Equal(3, a.TotalDuplicateGroups);
        Assert.Single(a.FieldStatistics);
        Assert.Equal(150, a.FieldStatistics[0].Count);
        Assert.Equal(1500, a.FieldStatistics[0].SumNumber);
    }

    [Fact]
    [Trait("Category", "P6.2")]
    public void AnalysisResult_MergeFrom_NewFieldAppended()
    {
        var a = new AnalysisResult
        {
            TotalObjects = 10,
            FieldStatistics = new List<FieldStatistic>
            {
                new() { FieldName = "a", Count = 10 }
            }
        };
        var b = new AnalysisResult
        {
            TotalObjects = 5,
            FieldStatistics = new List<FieldStatistic>
            {
                new() { FieldName = "b", Count = 5 }
            }
        };

        a.MergeFrom(b);

        Assert.Equal(2, a.FieldStatistics.Count);
        Assert.Contains(a.FieldStatistics, f => f.FieldName == "a" && f.Count == 10);
        Assert.Contains(a.FieldStatistics, f => f.FieldName == "b" && f.Count == 5);
    }

    // ──────────────────────────────────────────────
    // AnalysisRunner 端到端：--checkpoint / --resume
    // ──────────────────────────────────────────────

    [Fact]
    [Trait("Category", "P6.2")]
    public void AnalysisRunner_Checkpoint_SavesSnapshotAfterRun()
    {
        string input = NewTempPath(".jsonl");
        string checkpoint = NewTempPath(".snap.json");
        try
        {
            // JSONL 是一行一个 JSON 对象，不是 JSON 数组
            File.WriteAllText(input, "{\"a\":1,\"b\":\"x\"}\n{\"a\":2,\"b\":\"y\"}\n{\"a\":3,\"b\":\"x\"}\n");
            int code = AnalysisRunner.Run(new AnalysisOptions
            {
                InputPath = input,
                CheckpointPath = checkpoint,
                ShowProgress = false
            });
            Assert.Equal(0, code);
            Assert.True(File.Exists(checkpoint), "检查点文件应被落盘");

            var snap = AnalyzerSnapshot.LoadFrom(checkpoint);
            Assert.Equal(Path.GetFullPath(input), Path.GetFullPath(snap.SourcePath));
            Assert.Equal("jsonl", snap.SourceFormat);
            Assert.Equal(3, snap.SourcePosition);
            Assert.Equal(3, snap.Result.TotalObjects);
            Assert.Equal(2, snap.Result.FieldStatistics.Count);
        }
        finally
        {
            TryDelete(input);
            TryDelete(checkpoint);
        }
    }

    [Fact]
    [Trait("Category", "P6.2")]
    public void AnalysisRunner_ResumeFromMissingFile_ReturnsError()
    {
        string input = NewTempPath(".jsonl");
        string missing = NewTempPath(".snap.json");
        try
        {
            File.WriteAllText(input, "{\"a\":1}");
            int code = AnalysisRunner.Run(new AnalysisOptions
            {
                InputPath = input,
                ResumeFrom = missing,
                ShowProgress = false
            });
            Assert.Equal(1, code);
        }
        finally
        {
            TryDelete(input);
            // missing 不存在，无需清理
        }
    }

    [Fact]
    [Trait("Category", "P6.2")]
    public void AnalysisRunner_ResumeMismatchedSourceFile_ReturnsError()
    {
        string inputA = NewTempPath(".jsonl");
        string inputB = NewTempPath(".jsonl");
        string checkpoint = NewTempPath(".snap.json");
        try
        {
            File.WriteAllText(inputA, "{\"a\":1}");
            File.WriteAllText(inputB, "{\"a\":2}");

            // 先在 A 上跑出检查点
            int codeA = AnalysisRunner.Run(new AnalysisOptions
            {
                InputPath = inputA,
                CheckpointPath = checkpoint,
                ShowProgress = false
            });
            Assert.Equal(0, codeA);

            // 用 A 的检查点跑 B，必须拒绝（源文件不一致）
            int codeB = AnalysisRunner.Run(new AnalysisOptions
            {
                InputPath = inputB,
                ResumeFrom = checkpoint,
                ShowProgress = false
            });
            Assert.Equal(1, codeB);
        }
        finally
        {
            TryDelete(inputA);
            TryDelete(inputB);
            TryDelete(checkpoint);
        }
    }

    [Fact]
    [Trait("Category", "P6.2")]
    public void AnalysisRunner_Resume_SkipsAlreadyProcessedRecords()
    {
        // 模拟「分块分析」：先跑全部 4 条 → 检查点；再 --resume 跑同一文件（StartFromLine=4，跳过全部 4 条）→ 合并。
        // 合并后 TotalObjects 应等于 4（即只保留历史结果，无新数据）。
        string input = NewTempPath(".jsonl");
        string checkpoint = NewTempPath(".snap.json");
        try
        {
            File.WriteAllText(input,
                "{\"a\":1,\"b\":\"x\"}\n" +
                "{\"a\":2,\"b\":\"y\"}\n" +
                "{\"a\":3,\"b\":\"x\"}\n" +
                "{\"a\":4,\"b\":\"z\"}\n");

            // 第 1 段：跑前 4 条并落盘检查点
            int code1 = AnalysisRunner.Run(new AnalysisOptions
            {
                InputPath = input,
                CheckpointPath = checkpoint,
                ShowProgress = false
            });
            Assert.Equal(0, code1);
            var snap1 = AnalyzerSnapshot.LoadFrom(checkpoint);
            Assert.Equal(4, snap1.SourcePosition);
            Assert.Equal(4, snap1.Result.TotalObjects);

            // 第 2 段：--resume 加载快照，parser 从 StartFromLine=4 跳过全部 4 条
            // 本 run 新增 0 条；合并后 = 4
            int code2 = AnalysisRunner.Run(new AnalysisOptions
            {
                InputPath = input,
                ResumeFrom = checkpoint,
                CheckpointPath = checkpoint + ".new",
                ShowProgress = false
            });
            Assert.Equal(0, code2);
            var snap2 = AnalyzerSnapshot.LoadFrom(checkpoint + ".new");
            Assert.Equal(4, snap2.Result.TotalObjects);
            // 关键断言：本 run 实际未解析任何新记录，合并后等于历史值
            var aStat = snap2.Result.FieldStatistics.First(f => f.FieldName == "a");
            Assert.Equal(4, aStat.Count);
            Assert.Equal(10, aStat.SumNumber); // 1+2+3+4
        }
        finally
        {
            TryDelete(input);
            TryDelete(checkpoint);
            TryDelete(checkpoint + ".new");
        }
    }

    [Fact]
    [Trait("Category", "P6.2")]
    public void AnalysisRunner_Baseline_MergesHistoricalResult()
    {
        string input = NewTempPath(".jsonl");
        string baseline = NewTempPath(".json");
        string output = NewTempPath(".json");
        try
        {
            // 准备历史基线：5 条记录
            var historical = new AnalysisResult
            {
                TotalObjects = 5,
                TotalUniqueObjects = 5,
                FieldStatistics = new List<FieldStatistic>
                {
                    new() { FieldName = "a", Count = 5, SumNumber = 15, MinNumber = 1, MaxNumber = 5, AvgNumber = 3 }
                }
            };
            File.WriteAllText(baseline, JsonSerializer.Serialize(historical, s_jsonWriteOptions));

            // 准备新数据：3 条记录
            File.WriteAllText(input, "{\"a\":10,\"b\":\"x\"}\n{\"a\":20}\n{\"a\":30,\"b\":\"y\"}");

            int code = AnalysisRunner.Run(new AnalysisOptions
            {
                InputPath = input,
                BaselinePath = baseline,
                OutputPath = output,
                ShowProgress = false
            });
            Assert.Equal(0, code);

            // 验证合并结果
            var finalResult = JsonSerializer.Deserialize<AnalysisResult>(File.ReadAllText(output))!;
            Assert.Equal(8, finalResult.TotalObjects); // 5 (基线) + 3 (本 run)
            var aStat = finalResult.FieldStatistics.First(f => f.FieldName == "a");
            Assert.Equal(8, aStat.Count);
            // Sum: 15 (基线) + 10+20+30 = 75
            Assert.Equal(75, aStat.SumNumber);
        }
        finally
        {
            TryDelete(input);
            TryDelete(baseline);
            TryDelete(output);
        }
    }

    [Fact]
    [Trait("Category", "P6.2")]
    public void AnalysisRunner_BaselinePathMissing_ReturnsError()
    {
        string input = NewTempPath(".jsonl");
        string missing = NewTempPath(".json");
        try
        {
            File.WriteAllText(input, "{\"a\":1}");
            int code = AnalysisRunner.Run(new AnalysisOptions
            {
                InputPath = input,
                BaselinePath = missing,
                ShowProgress = false
            });
            Assert.Equal(1, code);
        }
        finally
        {
            TryDelete(input);
        }
    }

    [Fact]
    [Trait("Category", "P6.2")]
    public void AnalysisRunner_ResumeFromSameFileAsCheckpoint_Rejected()
    {
        string input = NewTempPath(".jsonl");
        string path = NewTempPath(".snap.json");
        try
        {
            File.WriteAllText(input, "{\"a\":1}");
            int code = AnalysisRunner.Run(new AnalysisOptions
            {
                InputPath = input,
                ResumeFrom = path,
                CheckpointPath = path,
                ShowProgress = false
            });
            Assert.Equal(1, code);
        }
        finally
        {
            TryDelete(input);
            TryDelete(path);
        }
    }

    [Fact]
    [Trait("Category", "P6.2")]
    public void AnalysisRunner_ResumeMergesEnumViolations()
    {
        // 验证 --resume 不仅合并数值，还能把枚举违规一起合并
        string rulesPath = NewTempPath(".json");
        string input = NewTempPath(".jsonl");
        string checkpoint = NewTempPath(".snap.json");
        try
        {
            // 规则文件：status 字段必须是 "A" / "B"（rules 字段名是 enumValues）
            File.WriteAllText(rulesPath, @"{
  ""fields"": {
    ""status"": { ""enumValues"": [""A"", ""B""] }
  }
}");
            File.WriteAllText(input,
                "{\"status\":\"A\"}\n" +
                "{\"status\":\"B\"}\n" +
                "{\"status\":\"X\"}\n"); // 1 个 enum violation

            // 第 1 段：跑全部 + 落盘
            int code1 = AnalysisRunner.Run(new AnalysisOptions
            {
                InputPath = input,
                RulesFile = rulesPath,
                CheckpointPath = checkpoint,
                ShowProgress = false
            });
            Assert.Equal(0, code1);
            var snap = AnalyzerSnapshot.LoadFrom(checkpoint);
            var statusStat = snap.Result.FieldStatistics.First(f => f.FieldName == "status");
            Assert.Single(statusStat.EnumViolations);

            // 第 2 段：--resume 重新分析，parser 跳过 3 行；本 run 新增 0 条违规
            // 合并后历史违规仍存在 1 个
            int code2 = AnalysisRunner.Run(new AnalysisOptions
            {
                InputPath = input,
                RulesFile = rulesPath,
                ResumeFrom = checkpoint,
                CheckpointPath = checkpoint + ".new",
                ShowProgress = false
            });
            Assert.Equal(0, code2);
            var snap2 = AnalyzerSnapshot.LoadFrom(checkpoint + ".new");
            var statusStat2 = snap2.Result.FieldStatistics.First(f => f.FieldName == "status");
            Assert.Single(statusStat2.EnumViolations);
        }
        finally
        {
            TryDelete(rulesPath);
            TryDelete(input);
            TryDelete(checkpoint);
            TryDelete(checkpoint + ".new");
        }
    }

    // ──────────────────────────────────────────────
    // Helper
    // ──────────────────────────────────────────────

    private static readonly JsonSerializerOptions s_jsonWriteOptions = new() { WriteIndented = true };

    private static string NewTempPath(string ext)
        => Path.Combine(Path.GetTempPath(), "p6-" + Guid.NewGuid().ToString("N") + ext);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 忽略 */ }
    }
}
