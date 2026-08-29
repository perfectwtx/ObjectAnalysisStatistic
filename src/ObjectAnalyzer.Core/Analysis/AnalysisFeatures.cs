namespace ObjectAnalyzer.Analysis;

/// <summary>
/// P4 分析深度增强的可选特性开关。通过 <see cref="ObjectAnalyzer.Analyze"/> 注入；全 false 时
/// （默认值）行为与 v1.x 一致，无任何额外开销。
///
/// 启用任一特性会增加内存占用（如 FieldCorrelationCollector 蓄水池默认 20K 行）；
/// CLI 入口（<see cref="AnalysisOptions"/>）将对应的字段映射到这些开关。
/// </summary>
public sealed class AnalysisFeatures
{
    /// <summary>
    /// 是否在流式阶段收集数值字段样本，最终输出 Pearson/Spearman 字段相关性矩阵。
    /// </summary>
    public bool EnableCorrelation { get; set; }

    /// <summary>
    /// 相关性收集器蓄水池容量（每字段保留最多 N 行数值）。默认 20000。
    /// </summary>
    public int CorrelationReservoir { get; set; } = 20_000;

    /// <summary>
    /// 是否对字符串字段归纳形态模板（"###-####" / "[a-z]+@[a-z]+" 等）。
    /// </summary>
    public bool EnableStringPatterns { get; set; }

    /// <summary>
    /// 是否对字符串字段做 Unicode 卫生检查（全/半角、不可见字符、控制字符、大小写混用等）。
    /// </summary>
    public bool EnableUnicodeHygiene { get; set; }

    /// <summary>
    /// 是否对 DateTime 字段做时间序列深度分析（趋势、周期、间隙、脉冲性）。
    /// </summary>
    public bool EnableTimeSeries { get; set; }

    /// <summary>
    /// 是否导出分布快照（用于 --diff-distribution）。
    /// </summary>
    public bool EnableDistributionSnapshot { get; set; }

    /// <summary>
    /// 是否计算采样置信区间（启用 sampling 时自动启用）。
    /// </summary>
    public bool EnableSamplingConfidence { get; set; } = true;

    /// <summary>任一特性启用则返回 true。</summary>
    public bool AnyEnabled =>
        EnableCorrelation ||
        EnableStringPatterns ||
        EnableUnicodeHygiene ||
        EnableTimeSeries ||
        EnableDistributionSnapshot;

    /// <summary>所有开关均为 false（默认）时返回 true。</summary>
    public bool IsEmpty => !AnyEnabled;
}
