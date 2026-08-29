using global::ObjectAnalyzer.Analysis;

namespace ObjectAnalyzer.Web.Tests;

/// <summary>
/// P6.1 / P6.3 进度回调 + 内存预算的回调工厂测试。
/// MemoryBudget.Check 内部使用 AnsiConsole.MarkupLine——加入顺序集合避免 Spectre.Console 报并发异常。
/// </summary>
[Collection(WebAnsiConsoleSequentialGroup.Name)]
public class P6ProgressReporterTests
{
    // ──────────────────────────────────────────────
    // 参数校验
    // ──────────────────────────────────────────────

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

    // ──────────────────────────────────────────────
    // showProgress 开关
    // ──────────────────────────────────────────────

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
        // 0 MB budget 不会触发警告
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

        // 关掉进度条 → progressTextSink 不应被调用
        Assert.Empty(progressTexts);
    }

    // ──────────────────────────────────────────────
    // 内存预算：禁用 / soft 超限 / hard 超限
    // ──────────────────────────────────────────────

    [Fact]
    [Trait("Category", "P6.3")]
    public void Build_DisabledBudget_NeverCallsMemoryWarningSink()
    {
        // MaxMemoryMB = 0 → IsEnabled=false，Check 是 no-op
        var budget = new MemoryBudget(0, MaxMemoryMode.Soft);
        var progressTexts = new List<string>();
        var memoryWarnings = new List<string>();

        var callback = P6ProgressReporter.Build(
            showProgress: true,
            budget: budget,
            progressTextSink: text => progressTexts.Add(text),
            memoryWarningSink: warn => memoryWarnings.Add(warn),
            localize: (key, args) => $"{key}");

        // 多次调用都不应触发警告
        for (long i = 0; i < 50; i++) callback(i);
        Assert.Empty(memoryWarnings);
    }

    [Fact]
    [Trait("Category", "P6.3")]
    public void Build_SoftMode_UnderLimit_NeverCallsMemoryWarningSink()
    {
        // 给一个极大但不会溢出的阈值。MemoryBudget 内部 _thresholdBytes = maxMemoryMB * 1024 * 1024，
        // 若直接用 long.MaxValue 会溢出成极小值，导致永远"超限"。先用 long.MaxValue / (1024*1024) 拿
        // 一个合法的大约 8.5 EB 上限，保证在测试进程内存尺度下绝对不可能超限。
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
        // 1MB 阈值对 .NET 进程而言几乎必然超限
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

        // soft 模式首次超限触发一次警告，之后 MemoryBudget 内部 _alreadyWarned 防抖
        Assert.Single(memoryWarnings);
        Assert.Equal("WarnMemorySoftExceeded", memoryWarnings[0]);
    }

    [Fact]
    [Trait("Category", "P6.3")]
    public void Build_SoftMode_OverLimit_StillUpdatesProgressWhenEnabled()
    {
        // showProgress + 超限：两个 sink 都被调用，验证内存护栏与进度条互不干扰
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
    }

    [Fact]
    [Trait("Category", "P6.3")]
    public void Build_HardMode_OverLimit_DoesNotThrowInCallback_AndDoesNotCallWarningSink()
    {
        // 设计要点：hard 模式不能在 progress 回调里抛（Progress.Report 是 thread-pool 派发），
        // 只能设 HardLimitExceeded=true，由分析循环显式调用 ThrowIfHardExceeded。
        // 因此 memoryWarningSink 在 hard 模式下不应被调用——警告只属于 soft 模式。
        var budget = new MemoryBudget(1, MaxMemoryMode.Hard);
        var progressTexts = new List<string>();
        var memoryWarnings = new List<string>();

        var callback = P6ProgressReporter.Build(
            showProgress: false,
            budget: budget,
            progressTextSink: _ => { },
            memoryWarningSink: warn => memoryWarnings.Add(warn),
            localize: (_, _) => "x");

        // 不应抛任何异常
        var ex = Record.Exception(() =>
        {
            callback(1);
            callback(2);
            callback(3);
        });
        Assert.Null(ex);

        // hard 模式：memoryWarningSink 不应被调用
        Assert.Empty(memoryWarnings);
        // 但 HardLimitExceeded 应被设上
        Assert.True(budget.HardLimitExceeded);
    }

    [Fact]
    [Trait("Category", "P6.3")]
    public void Build_Localize_PassesArgsThrough()
    {
        // localize 收到的 args 应当与 count 一致（不是 null / 长度不对）
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
