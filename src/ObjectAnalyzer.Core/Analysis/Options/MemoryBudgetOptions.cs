namespace ObjectAnalyzer.Analysis.Options;

/// <summary>
/// 进程托管堆内存预算监控：上限 + 超限行为。
/// </summary>
public sealed class MemoryBudgetOptions
{
    /// <summary>进程托管堆上限（MB，--max-memory &lt;MB&gt;）。0 = 不监控。仅监控 .NET 托管堆（GC.GetTotalMemory）。</summary>
    public long MaxMemoryMB { get; set; }

    /// <summary>超限行为（<c>--max-memory-mode soft|hard</c>）。默认 <see cref="MaxMemoryMode.Soft"/>。</summary>
    public MaxMemoryMode MaxMemoryMode { get; set; } = MaxMemoryMode.Soft;
}
