namespace ObjectAnalyzer.Analysis;

/// <summary>
/// Observes the process-wide managed heap, not RSS or a strict allocation ceiling.
/// Soft mode is advisory: the pipeline selects sampling before reading, never midway through a population.
/// Hard mode fails at read/record/batch checkpoints. Third-party calls can allocate between checkpoints.
/// Independent finite resource limits apply in both modes, including when heap monitoring is disabled.
/// </summary>
public sealed class MemoryBudget(long maxMemoryMB, MaxMemoryMode mode, Func<long>? measureManagedBytes = null)
{
    /// <summary>内存上限（MB）。0 表示不启用监控。</summary>
    public long MaxMemoryMB { get; } = maxMemoryMB < 0
        ? throw new ArgumentOutOfRangeException(nameof(maxMemoryMB), "maxMemoryMB 不能为负数")
        : maxMemoryMB;

    /// <summary>超限行为模式（soft / hard）。</summary>
    public MaxMemoryMode Mode { get; } = mode;

    private readonly long _thresholdBytes = maxMemoryMB > long.MaxValue / (1024L * 1024) ? long.MaxValue : maxMemoryMB * 1024L * 1024L;
    private bool _alreadyWarned; // soft 模式下仅打印一次警告
    private bool _samplingTriggered; // soft 模式下仅触发一次自动启用采样
    private long _peakBytes; // 用于审计/报告：本 run 内存峰值

    /// <summary>控制台输出（soft 模式超限时打印警告）。默认静默，可由宿主注入。</summary>
    public void SetConsole(IAnalysisConsole? console) => _console = console ?? SilentConsole.Instance;
    private IAnalysisConsole _console = SilentConsole.Instance;

    /// <summary>本次 run 是否启用了内存监控。</summary>
    public bool IsEnabled => MaxMemoryMB > 0;

    /// <summary>本 run 观测到的最大托管堆字节数（peak）。</summary>
    public long PeakBytes => _peakBytes;

    /// <summary>本 run 观测到的最大托管堆 MB（peak）。</summary>
    public double PeakMB => _peakBytes / 1024.0 / 1024.0;

    private readonly object _sync = new();

    /// <summary>
    /// Read/record/batch checkpoint. Check records pressure; ThrowIfHardExceeded enforces hard mode.
    ///
    /// - 未启用（MaxMemoryMB == 0）：无操作。
    /// - 未超限：仅更新 peak。
    /// - 已超限：
    ///   - soft：第一次超限时打印警告并把 <paramref name="samplingAutoEnable"/> 置为 true（仅为兼容的压力信号）；
    ///     此后不再重复打印，但持续更新 peak 供报告使用。
    ///   - hard：设置 <see cref="HardLimitExceeded"/> = true。调用方应在分析循环中检查该标记并以
    ///     <see cref="ThrowIfHardExceeded"/> 显式抛错。
    ///     不能直接抛——<c>System.Progress&lt;T&gt;.Report</c> 在 thread-pool 线程上派发回调，
    ///     抛出的异常会成为"未观察异常"逃出主流程。
    /// </summary>
    /// <param name="objectCount">当前已分析对象数（用于警告信息中给出上下文）。</param>
    /// <param name="samplingAutoEnable">Legacy first-pressure signal. Never change sampling after input has started.</param>
    public void Check(long objectCount, out bool samplingAutoEnable)
    {
        lock (_sync) CheckCore(objectCount, out samplingAutoEnable);
    }

    private void CheckCore(long objectCount, out bool samplingAutoEnable)
    {
        samplingAutoEnable = false;
        if (!IsEnabled) return;

        long current = measureManagedBytes?.Invoke() ?? GC.GetTotalMemory(forceFullCollection: false);
        if (current > _peakBytes) _peakBytes = current;

        if (current <= _thresholdBytes) return;

        // 超限处理
        double currentMB = current / 1024.0 / 1024.0;
        if (Mode == MaxMemoryMode.Hard)
        {
            // 记录超限事实，不在 progress 回调中抛（见 XML 注释）
            HardLimitExceeded = true;
            _hardLimitMessage =
                $"内存使用 {currentMB:F1} MB 超出 --max-memory {MaxMemoryMB} MB 硬上限（已分析 {objectCount:N0} 条对象）";
            return;
        }

        // soft 模式
        if (!_alreadyWarned)
        {
            _alreadyWarned = true;
            _samplingTriggered = true;
            samplingAutoEnable = true;
            _console.MarkupLine(
                $"[yellow][[内存]][/] 警告: 内存使用 [red]{currentMB:F1} MB[/] 已超出 --max-memory {MaxMemoryMB:N0} MB 软阈值 " +
                $"(已分析 {objectCount:N0} 条对象)。软阈值仅作观测；采样策略在读取输入前确定，独立的资源限制仍然生效。");
        }
    }

    /// <summary>hard 模式超限标记。<see cref="Check"/> 在超限时设为 true；分析循环应检查此标记。</summary>
    public bool HardLimitExceeded { get; private set; }

    /// <summary>hard 模式超限消息（在第一次超限时冻结）。</summary>
    public string? HardLimitMessage => _hardLimitMessage;
    private string? _hardLimitMessage;

    /// <summary>
    /// 如果 <see cref="HardLimitExceeded"/> 为 true，抛 <see cref="MaxMemoryExceededException"/>。
    /// 由分析循环（每分块结束后）调用以避免在 progress 回调线程上抛异常。
    /// </summary>
    public void ThrowIfHardExceeded()
    {
        if (HardLimitExceeded)
        {
            throw new MaxMemoryExceededException(
                _hardLimitMessage ?? $"内存使用超出 --max-memory {MaxMemoryMB} MB 硬上限");
        }
    }

    /// <summary>Legacy pressure flag; sampling is selected separately before input starts.</summary>
    public bool SamplingTriggered => _samplingTriggered;
}
