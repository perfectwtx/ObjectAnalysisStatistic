namespace ObjectAnalyzer.Analysis.Options;

/// <summary>
/// 采样策略：蓄水池随机抽样 + 等距系统抽样（互斥）+ 采样置信区间宽度阈值 + 内存压力回退容量。
/// </summary>
public sealed class SamplingOptions
{
    /// <summary>蓄水池随机抽样容量。0 = 不启用。与 <c>--sample-reservoir</c> 对应。</summary>
    public int SampleReservoir { get; set; }

    /// <summary>等距系统抽样步长。0 = 不启用。与 <c>--sample-step</c> 对应。</summary>
    public int SampleStep { get; set; }

    /// <summary>采样置信区间宽度阈值（覆盖区间宽度 ≥ 此值视为"低可信"，默认 0.2）。</summary>
    public double LowConfidenceWidth { get; set; } = 0.2;

    /// <summary>soft 模式下被自动触发的采样容量（仅当用户未指定 SampleReservoir/SampleStep 时生效）。默认 50_000 条。</summary>
    public int MemoryFallbackSampleSize { get; set; } = 50_000;
}
