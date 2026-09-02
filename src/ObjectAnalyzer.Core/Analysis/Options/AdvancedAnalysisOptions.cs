namespace ObjectAnalyzer.Analysis.Options;

/// <summary>
/// 深度分析 深度分析开关集合（默认全关，开启会增加运行时间与内存）。
/// </summary>
public sealed class AdvancedAnalysisOptions
{
    /// <summary>启用字段相关性矩阵分析（Pearson / Spearman，--correlation）。</summary>
    public bool EnableCorrelation { get; set; }

    /// <summary>启用字符串模式自动归纳（--string-patterns）。</summary>
    public bool EnableStringPatterns { get; set; }

    /// <summary>启用 Unicode 卫生检查（--unicode-hygiene）。</summary>
    public bool EnableUnicodeHygiene { get; set; }

    /// <summary>启用时间序列深度分析（--time-series）。</summary>
    public bool EnableTimeSeries { get; set; }

    /// <summary>模糊去重目标字段（--fuzzy-dedup-field &lt;FIELD&gt;）。空 = 不启用。</summary>
    public string? FuzzyDedupField { get; set; }

    /// <summary>启用 P3 分布快照（--distribution-snapshot，等价于设置 DistributionDrift.DistributionSnapshotPath）。</summary>
    public bool EnableDistributionSnapshot { get; set; }

    /// <summary>相关性收集器蓄水池容量（每字段保留最多 N 行数值）。默认 20000。</summary>
    public int CorrelationReservoir { get; set; } = 20_000;
}
