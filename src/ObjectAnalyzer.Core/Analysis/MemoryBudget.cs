using System;
using System.Threading;
using Spectre.Console;

namespace ObjectAnalyzer.Analysis;

/// <summary>
/// P6.3：内存预算监控。
///
/// 用途：
/// 解决 50GB+ JSONL 全量物化导致 OOM 的问题。--max-memory &lt;MB&gt; 让用户给本进程设一个堆上限，
/// 运行时周期采样 GC 总占用，超出阈值时按 <see cref="MaxMemoryMode"/> 行为：
/// - <c>soft</c>（默认）：打印黄色警告并自动启用采样（--sample-reservoir 50_000），
///   让分析在有限的内存预算下继续推进。
/// - <c>hard</c>：抛出 <see cref="MaxMemoryExceededException"/>，AnalysisRunner 捕获后以退出码 1 退出。
///
/// 设计取舍：
/// - 仅监控 .NET 托管堆（GC.GetTotalMemory / GC.GetTotalAllocatedBytes），
///   不监控非托管堆 / 第三方 native 库；这是有意的——只覆盖我们能直接影响的代码路径。
/// - 采样频率：每个分析批次（解析 N 条对象）结束后调用一次 <see cref="Check"/>；
///   100% 精准"实时"监控意义不大，因为 GC 是 lazy 的。
/// - 一次 run 内只触发一次模式切换（soft 模式进入采样后不再退出），
///   避免反复抖动（启用采样 → 内存下降 → 关闭采样 → 内存上升 → ...）。
/// </summary>
public sealed class MemoryBudget(long maxMemoryMB, MaxMemoryMode mode)
{
    /// <summary>内存上限（MB）。0 表示不启用监控。</summary>
    public long MaxMemoryMB { get; } = maxMemoryMB < 0
        ? throw new ArgumentOutOfRangeException(nameof(maxMemoryMB), "maxMemoryMB 不能为负数")
        : maxMemoryMB;

    /// <summary>超限行为模式（soft / hard）。</summary>
    public MaxMemoryMode Mode { get; } = mode;

    private readonly long _thresholdBytes = maxMemoryMB * 1024L * 1024L;
    private bool _alreadyWarned; // soft 模式下仅打印一次警告
    private bool _samplingTriggered; // soft 模式下仅触发一次自动启用采样
    private long _peakBytes; // 用于审计/报告：本 run 内存峰值

    /// <summary>本次 run 是否启用了内存监控。</summary>
    public bool IsEnabled => MaxMemoryMB > 0;

    /// <summary>本 run 观测到的最大托管堆字节数（peak）。</summary>
    public long PeakBytes => _peakBytes;

    /// <summary>本 run 观测到的最大托管堆 MB（peak）。</summary>
    public double PeakMB => _peakBytes / 1024.0 / 1024.0;

    /// <summary>
    /// 周期采样点：每分析完一批记录后调用一次。
    ///
    /// - 未启用（MaxMemoryMB == 0）：无操作。
    /// - 未超限：仅更新 peak。
    /// - 已超限：
    ///   - soft：第一次超限时打印警告并把 <paramref name="samplingAutoEnable"/> 置为 true（让 AnalysisRunner 自动启用采样）；
    ///     此后不再重复打印，但持续更新 peak 供报告使用。
    ///   - hard：设置 <see cref="HardLimitExceeded"/> = true。调用方应在分析循环中检查该标记并以
    ///     <see cref="ThrowIfHardExceeded"/> 显式抛错。
    ///     不能直接抛——<c>System.Progress&lt;T&gt;.Report</c> 在 thread-pool 线程上派发回调，
    ///     抛出的异常会成为"未观察异常"逃出主流程。
    /// </summary>
    /// <param name="objectCount">当前已分析对象数（用于警告信息中给出上下文）。</param>
    /// <param name="samplingAutoEnable">out：当 soft 模式自动启用采样时返回 true；调用方据此设置 SampleReservoir。</param>
    public void Check(long objectCount, out bool samplingAutoEnable)
    {
        samplingAutoEnable = false;
        if (!IsEnabled) return;

        long current = GC.GetTotalMemory(forceFullCollection: false);
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
            AnsiConsole.MarkupLine(
                $"[yellow][[内存]][/] 警告: 内存使用 [red]{currentMB:F1} MB[/] 已超出 --max-memory {MaxMemoryMB:N0} MB 软阈值 " +
                $"(已分析 {objectCount:N0} 条对象)。自动启用蓄水池采样 50,000 条以保护进程，后续分析结果将基于采样而非全集。");
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

    /// <summary>soft 模式下是否已经触发过自动采样。用于 AnalysisRunner 判断是否需要回填 SampleReservoir。</summary>
    public bool SamplingTriggered => _samplingTriggered;
}
