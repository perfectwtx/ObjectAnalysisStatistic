using System.Text.Json;

namespace ObjectAnalyzer.Tests;

/// <summary>
/// P6.2 增量分析 / 断点续跑测试：
/// - AnalyzerSnapshot 的 SaveTo / LoadFrom 序列化
/// - 独立计算状态合并见 AccumulatorTests
/// - 报告模型不再提供续算合并
/// - AnalysisRunner --checkpoint / --resume / --baseline 端到端
/// - 错误路径（--resume 源文件不一致 / 快照不存在）
/// </summary>

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
                State = StateFor(Enumerable.Range(0, 12345).Select(i => new Dictionary<string, object?> { ["x"] = i % 3 })).Capture()
            };
            snapshot.SaveTo(path);

            var loaded = AnalyzerSnapshot.LoadFrom(path);
            Assert.Equal("object-analyzer.snapshot/v3", loaded.Schema);
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
            Assert.Contains("schema incompatible", ex.Message);
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
                Input = { InputPath = input },
                Incremental = { CheckpointPath = checkpoint },
                Progress = { ShowProgress = false }
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
                Input = { InputPath = input },
                Incremental = { ResumeFrom = missing },
                Progress = { ShowProgress = false }
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
                Input = { InputPath = inputA },
                Incremental = { CheckpointPath = checkpoint },
                Progress = { ShowProgress = false }
            });
            Assert.Equal(0, codeA);

            // 用 A 的检查点跑 B，必须拒绝（源文件不一致）
            int codeB = AnalysisRunner.Run(new AnalysisOptions
            {
                Input = { InputPath = inputB },
                Incremental = { ResumeFrom = checkpoint },
                Progress = { ShowProgress = false }
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
                Input = { InputPath = input },
                Incremental = { CheckpointPath = checkpoint },
                Progress = { ShowProgress = false }
            });
            Assert.Equal(0, code1);
            var snap1 = AnalyzerSnapshot.LoadFrom(checkpoint);
            Assert.Equal(4, snap1.SourcePosition);
            Assert.Equal(4, snap1.Result.TotalObjects);

            // 第 2 段：--resume 加载快照，parser 从 StartFromLine=4 跳过全部 4 条
            // 本 run 新增 0 条；合并后 = 4
            int code2 = AnalysisRunner.Run(new AnalysisOptions
            {
                Input = { InputPath = input },
                Incremental = { ResumeFrom = checkpoint, CheckpointPath = checkpoint + ".new" },
                Progress = { ShowProgress = false }
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
            var historical = StateFor(Enumerable.Range(1, 5).Select(i => new Dictionary<string, object?> { ["a"] = i }));
            new AnalyzerSnapshot { State = historical.Capture() }.SaveTo(baseline);

            // 准备新数据：3 条记录
            File.WriteAllText(input, "{\"a\":10,\"b\":\"x\"}\n{\"a\":20}\n{\"a\":30,\"b\":\"y\"}");

            int code = AnalysisRunner.Run(new AnalysisOptions
            {
                Input = { InputPath = input },
                Incremental = { BaselinePath = baseline },
                Report = { OutputPath = output },
                Progress = { ShowProgress = false }
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
                Input = { InputPath = input },
                Incremental = { BaselinePath = missing },
                Progress = { ShowProgress = false }
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
                Input = { InputPath = input },
                Incremental = { ResumeFrom = path, CheckpointPath = path },
                Progress = { ShowProgress = false }
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
                Input = { InputPath = input },
                Rules = { RulesFile = rulesPath },
                Incremental = { CheckpointPath = checkpoint },
                Progress = { ShowProgress = false }
            });
            Assert.Equal(0, code1);
            var snap = AnalyzerSnapshot.LoadFrom(checkpoint);
            var statusStat = snap.Result.FieldStatistics.First(f => f.FieldName == "status");
            Assert.Single(statusStat.EnumViolations);

            // 第 2 段：--resume 重新分析，parser 跳过 3 行；本 run 新增 0 条违规
            // 合并后历史违规仍存在 1 个
            int code2 = AnalysisRunner.Run(new AnalysisOptions
            {
                Input = { InputPath = input },
                Rules = { RulesFile = rulesPath },
                Incremental = { ResumeFrom = checkpoint, CheckpointPath = checkpoint + ".new" },
                Progress = { ShowProgress = false }
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

    private static AnalysisAccumulator StateFor(IEnumerable<Dictionary<string, object?>> rows)
    {
        var state = new AnalysisAccumulator();
        state.ObserveRange(rows);
        return state;
    }

    private static string NewTempPath(string ext)
        => Path.Combine(Path.GetTempPath(), "p6-" + Guid.NewGuid().ToString("N") + ext);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 忽略 */ }
    }
}
