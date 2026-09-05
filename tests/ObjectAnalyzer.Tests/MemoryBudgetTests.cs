namespace ObjectAnalyzer.Tests;

/// <summary>
/// P6.3 内存预算测试：--max-memory / --max-memory-mode soft|hard
/// 覆盖单元测试（MemoryBudget 类本身）+ 端到端测试（AnalysisRunner 集成）。
/// </summary>

public class MemoryBudgetTests
{
    // ──────────────────────────────────────────────
    // MemoryBudget 单元测试
    // ──────────────────────────────────────────────

    [Fact]
    [Trait("Category", "P6.3")]
    public void MemoryBudget_NotEnabled_WhenMaxMemoryMBIsZero()
    {
        var budget = new MemoryBudget(0, MaxMemoryMode.Soft);
        Assert.False(budget.IsEnabled);
        budget.Check(0, out bool autoEnable);
        Assert.False(autoEnable);
    }

    [Fact]
    [Trait("Category", "P6.3")]
    public void MemoryBudget_SoftMode_DoesNotThrow_WhenOverLimit()
    {
        // 设一个极小阈值（1MB），任何运行都会超过
        var budget = new MemoryBudget(1, MaxMemoryMode.Soft);
        // 软模式：第一次超限返回 autoEnable=true，之后重复不再返回 true
        budget.Check(100, out bool first);
        Assert.True(first, "软模式首次超限应返回 autoEnable=true 以触发自动采样");
        budget.Check(200, out bool second);
        Assert.False(second, "软模式第二次不应重复触发自动采样");
        // 不抛异常
        Assert.True(budget.SamplingTriggered);
    }

    [Fact]
    [Trait("Category", "P6.3")]
    public void MemoryBudget_HardMode_Throws_WhenOverLimit()
    {
        // 设计：Check 仅在 thread-pool progress 回调里设标记，由分析循环显式调用 ThrowIfHardExceeded 抛异常。
        // 不能直接在 Check 里 throw——抛到 Progress.Report 会变成"未观察异常"逃出主流程。
        var budget = new MemoryBudget(1, MaxMemoryMode.Hard);
        budget.Check(100, out _);
        Assert.True(budget.HardLimitExceeded);
        var ex = Assert.Throws<MaxMemoryExceededException>(() => budget.ThrowIfHardExceeded());
        Assert.Contains("max-memory", ex.Message);
    }

    [Fact]
    [Trait("Category", "P6.3")]
    public void MemoryBudget_PeakBytes_TrackedAcrossChecks()
    {
        var budget = new MemoryBudget(long.MaxValue, MaxMemoryMode.Soft);
        budget.Check(0, out _);
        long peakAfterFirst = budget.PeakBytes;
        budget.Check(0, out _);
        long peakAfterSecond = budget.PeakBytes;
        // peak 是单调非降的
        Assert.True(peakAfterSecond >= peakAfterFirst);
    }

    [Fact]
    [Trait("Category", "P6.3")]
    public void MemoryBudget_NegativeMaxMemoryMB_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MemoryBudget(-1, MaxMemoryMode.Soft));
    }

    // ──────────────────────────────────────────────
    // AnalysisRunner 端到端
    // ──────────────────────────────────────────────

    [Fact]
    [Trait("Category", "P6.3")]
    public void AnalysisRunner_MaxMemory_HardMode_FailsOnTinyLimit()
    {
        string input = NewTempPath(".jsonl");
        try
        {
            File.WriteAllText(input, "{\"a\":1}\n{\"a\":2}\n{\"a\":3}\n");
            int code = AnalysisRunner.Run(new AnalysisOptions
            {
                Input = { InputPath = input },
                Memory = { MaxMemoryMB = 1, MaxMemoryMode = MaxMemoryMode.Hard },
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
    [Trait("Category", "P6.3")]
    public void AnalysisRunner_MaxMemory_GenerousLimit_Succeeds()
    {
        string input = NewTempPath(".jsonl");
        try
        {
            File.WriteAllText(input, "{\"a\":1}\n{\"a\":2}\n{\"a\":3}\n");
            int code = AnalysisRunner.Run(new AnalysisOptions
            {
                Input = { InputPath = input },
                Memory = { MaxMemoryMB = 1024, MaxMemoryMode = MaxMemoryMode.Hard },
                Progress = { ShowProgress = false }
            });
            Assert.Equal(0, code);
        }
        finally
        {
            TryDelete(input);
        }
    }

    [Fact]
    [Trait("Category", "P6.3")]
    public void AnalysisRunner_MaxMemoryZero_DisablesMonitoring()
    {
        // 0 MB = 不监控，应当与不传 --max-memory 行为一致
        string input = NewTempPath(".jsonl");
        try
        {
            File.WriteAllText(input, "{\"a\":1}");
            int code = AnalysisRunner.Run(new AnalysisOptions
            {
                Input = { InputPath = input },
                Memory = { MaxMemoryMB = 0 },
                Progress = { ShowProgress = false }
            });
            Assert.Equal(0, code);
        }
        finally
        {
            TryDelete(input);
        }
    }

    [Fact]
    [Trait("Category", "P6.3")]
    public void AnalysisRunner_MaxMemorySoft_AutoEnablesSampling_OnTinyLimit()
    {
        // 软模式 + 极小阈值 → 入口预检时如果检测到已超限（或极可能超限），自动启用采样
        // 1MB 阈值对于任何对象分析都几乎必然超限（运行时本身就要消耗几十 MB）
        string input = NewTempPath(".jsonl");
        try
        {
            // 写入一些数据让分析有点工作量
            for (int i = 0; i < 1000; i++)
            {
                File.AppendAllText(input, $"{{\"id\":{i},\"name\":\"n{i}\"}}\n");
            }
            int code = AnalysisRunner.Run(new AnalysisOptions
            {
                Input = { InputPath = input },
                Memory = { MaxMemoryMB = 1, MaxMemoryMode = MaxMemoryMode.Soft },
                Progress = { ShowProgress = false }
            });
            // 软模式不会失败。即使自动启用采样，分析也应当正常完成
            Assert.Equal(0, code);
        }
        finally
        {
            TryDelete(input);
        }
    }

    [Fact]
    [Trait("Category", "P6.3")]
    public void MaxMemoryExceededException_MessageContainsThreshold()
    {
        var ex = new MaxMemoryExceededException("测试：150 MB > 100 MB");
        Assert.Contains("150", ex.Message);
        Assert.Contains("100", ex.Message);
    }

    // ──────────────────────────────────────────────
    // Helper
    // ──────────────────────────────────────────────

    private static string NewTempPath(string ext)
        => Path.Combine(Path.GetTempPath(), "p63-" + Guid.NewGuid().ToString("N") + ext);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 忽略 */ }
    }
}
