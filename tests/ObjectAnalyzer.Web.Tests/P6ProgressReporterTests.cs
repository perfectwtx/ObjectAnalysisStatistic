using global::ObjectAnalyzer.Analysis;

namespace ObjectAnalyzer.Web.Tests;

/// <summary>
/// P6.1 / P6.3 进度回调 + 内存预算的回调工厂测试。
/// MemoryBudget.Check 内部使用 AnsiConsole.MarkupLine——加入顺序集合避免 Spectre.Console 报并发异常。
/// </summary>
[Collection(WebAnsiConsoleSequentialGroup.Name)]
public class P6ProgressReporterTests
{
    [Fact]
    [Trait("Category", "P6.1")]
    public void Build_NullBudget_Throws()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => P6ProgressReporter.Build(
            showProgress: true,
            budget: null!,
            progressTextSink: _ => { },
            memoryWarningSink: _ => { },
            localize: (_, _) => "x"));
        Assert.Equal("budget", ex.ParamName);
    }

    [Fact]
    [Trait("Category", "P6.1")]
    public void Build_NullProgressTextSink_Throws()
    {
        var budget = new MemoryBudget(0, MaxMemoryMode.Soft);
        var ex = Assert.Throws<ArgumentNullException>(() => P6ProgressReporter.Build(
            showProgress: true,
            budget: budget,
            progressTextSink: null!,
            memoryWarningSink: _ => { },
            localize: (_, _) => "x"));
        Assert.Equal("progressTextSink", ex.ParamName);
    }

    [Fact]
    [Trait("Category", "P6.1")]
    public void Build_NullMemoryWarningSink_Throws()
    {
        var budget = new MemoryBudget(0, MaxMemoryMode.Soft);
        var ex = Assert.Throws<ArgumentNullException>(() => P6ProgressReporter.Build(
            showProgress: true,
            budget: budget,
            progressTextSink: _ => { },
            memoryWarningSink: null!,
            localize: (_, _) => "x"));
        Assert.Equal("memoryWarningSink", ex.ParamName);
    }

    [Fact]
    [Trait("Category", "P6.1")]
    public void Build_NullLocalize_Throws()
    {
        var budget = new MemoryBudget(0, MaxMemoryMode.Soft);
        var ex = Assert.Throws<ArgumentNullException>(() => P6ProgressReporter.Build(
            showProgress: true,
            budget: budget,
            progressTextSink: _ => { },
            memoryWarningSink: _ => { },
            localize: null!));
        Assert.Equal("localize", ex.ParamName);
    }

    [Fact]
    [Trait("Category", "P6.1")]
    public void Build_ShowProgress_True_UpdatesProgressText()
    {
        var budget = new MemoryBudget(0, MaxMemoryMode.Soft);
        var progressTexts = new List<string>();
        var memoryWarnings = new List<string>();

        var callback = P6ProgressReporter.Build(
            showProgress: true,
            budget: budget,
            progressTextSink: text => progressTexts.Add(text),
            memoryWarningSink: warn => memoryWarnings.Add(warn),
            localize: (key, args) => $"{key}:{string.Join(",", args ?? Array.Empty<object?>())}");

        callback(123);
        callback(456);

        Assert.Equal(2, progressTexts.Count);
        Assert.Equal("ProgressAnalyzedN:123", progressTexts[0]);
        Assert.Equal("ProgressAnalyzedN:456", progressTexts[1]);
        Assert.Empty(memoryWarnings);
    }

    [Fact]
    [Trait("Category", "P6.1")]
    public void Build_ShowProgress_False_DoesNotCallProgressTextSink()
    {
        var budget = new MemoryBudget(0, MaxMemoryMode.Soft);
        var progressTexts = new List<string>();
        var memoryWarnings = new List<string>();

        var callback = P6ProgressReporter.Build(
            showProgress: false,
            budget: budget,
            progressTextSink: text => progressTexts.Add(text),
            memoryWarningSink: warn => memoryWarnings.Add(warn),
            localize: (_, _) => "should-not-appear");

        callback(10);
        callback(20);
        callback(30);

        Assert.Empty(progressTexts);
    }

    [Fact]
    [Trait("Category", "P6.3")]
    public void Build_DisabledBudget_NeverCallsMemoryWarningSink()
    {
        var budget = new MemoryBudget(0, MaxMemoryMode.Soft);
        var progressTexts = new List<string>();
        var memoryWarnings = new List<string>();

        var callback = P6ProgressReporter.Build(
            showProgress: true,
            budget: budget,
            progressTextSink: text => progressTexts.Add(text),
            memoryWarningSink: warn => memoryWarnings.Add(warn),
            localize: (key, args) => $"{key}");

        for (long i = 0; i < 50; i++) callback(i);
        Assert.Empty(memoryWarnings);
    }

    [Fact]
    [Trait("Category", "P6.3")]
    public void Build_SoftMode_UnderLimit_NeverCallsMemoryWarningSink()
    {
        long hugeMB = long.MaxValue / (1024L * 1024L);
        var budget = new MemoryBudget(hugeMB, MaxMemoryMode.Soft);
        var progressTexts = new List<string>();
        var memoryWarnings = new List<string>();

        var callback = P6ProgressReporter.Build(
            showProgress: true,
            budget: budget,
            progressTextSink: _ => { },
            memoryWarningSink: warn => memoryWarnings.Add(warn),
            localize: (_, _) => "x");

        callback(1);
        callback(2);
        callback(3);
        Assert.Empty(memoryWarnings);
    }

    [Fact]
    [Trait("Category", "P6.3")]
    public void Build_SoftMode_OverLimit_TriggersMemoryWarningOnce()
    {
        var budget = new MemoryBudget(1, MaxMemoryMode.Soft);
        var progressTexts = new List<string>();
        var memoryWarnings = new List<string>();

        var callback = P6ProgressReporter.Build(
            showProgress: false,
            budget: budget,
            progressTextSink: _ => { },
            memoryWarningSink: warn => memoryWarnings.Add(warn),
            localize: (key, _) => key);

        callback(100);
        callback(200);
        callback(300);
        callback(400);

        Assert.Single(memoryWarnings);
        Assert.Equal("WarnMemorySoftExceeded", memoryWarnings[0]);
    }

    [Fact]
    [Trait("Category", "P6.3")]
    public void Build_SoftMode_OverLimit_StillUpdatesProgressWhenEnabled()
    {
        // 显式制造超过 1MB 的托管堆压力，避免 CI 运行时因 GC 堆大小波动导致测试偶发不触发软阈值。
        var pressure = new byte[4 * 1024 * 1024];
        pressure[0] = 1;

        var budget = new MemoryBudget(1, MaxMemoryMode.Soft);
        var progressTexts = new List<string>();
        var memoryWarnings = new List<string>();

        var callback = P6ProgressReporter.Build(
            showProgress: true,
            budget: budget,
            progressTextSink: text => progressTexts.Add(text),
            memoryWarningSink: warn => memoryWarnings.Add(warn),
            localize: (key, args) => $"{key}:{string.Join(",", args ?? Array.Empty<object?>())}");

        callback(10);
        callback(20);

        Assert.Equal(2, progressTexts.Count);
        Assert.Equal("ProgressAnalyzedN:10", progressTexts[0]);
        Assert.Equal("ProgressAnalyzedN:20", progressTexts[1]);
        Assert.Single(memoryWarnings);
        GC.KeepAlive(pressure);
    }

    [Fact]
    [Trait("Category", "P6.3")]
    public void Build_HardMode_OverLimit_DoesNotThrowInCallback_AndDoesNotCallWarningSink()
    {
        var pressure = new byte[4 * 1024 * 1024];
        pressure[0] = 1;

        var budget = new MemoryBudget(1, MaxMemoryMode.Hard);
        var progressTexts = new List<string>();
        var memoryWarnings = new List<string>();

        var callback = P6ProgressReporter.Build(
            showProgress: false,
            budget: budget,
            progressTextSink: _ => { },
            memoryWarningSink: warn => memoryWarnings.Add(warn),
            localize: (_, _) => "x");

        var ex = Record.Exception(() =>
        {
            callback(1);
            callback(2);
            callback(3);
        });
        Assert.Null(ex);
        Assert.Empty(memoryWarnings);
        Assert.True(budget.HardLimitExceeded);
        GC.KeepAlive(pressure);
    }

    [Fact]
    [Trait("Category", "P6.3")]
    public void Build_Localize_PassesArgsThrough()
    {
        var budget = new MemoryBudget(0, MaxMemoryMode.Soft);
        string? observedKey = null;
        object?[]? observedArgs = null;

        var callback = P6ProgressReporter.Build(
            showProgress: true,
            budget: budget,
            progressTextSink: _ => { },
            memoryWarningSink: _ => { },
            localize: (key, args) =>
            {
                observedKey = key;
                observedArgs = args;
                return "rendered";
            });

        callback(777);
        Assert.Equal("ProgressAnalyzedN", observedKey);
        Assert.NotNull(observedArgs);
        Assert.Single(observedArgs!);
        Assert.Equal(777L, observedArgs![0]);
    }
}
