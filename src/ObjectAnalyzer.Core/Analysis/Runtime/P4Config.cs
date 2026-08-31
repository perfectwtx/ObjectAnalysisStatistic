using System.Text.Json.Serialization;

namespace ObjectAnalyzer.Analysis;

/// <summary>
/// P4 深度分析开关集合（默认全关，开启会增加运行时间与内存）。
/// </summary>
public class P4Config
{
    /// <summary>Pearson + Spearman 相关性矩阵。与 <c>--correlation</c> 对应。</summary>
    [JsonPropertyName("correlation")]
    public bool? Correlation { get; set; }

    /// <summary>字符串形态模板归纳（如 <c>###-####</c>）。与 <c>--string-patterns</c> 对应。</summary>
    [JsonPropertyName("stringPatterns")]
    public bool? StringPatterns { get; set; }

    /// <summary>Unicode 卫生检查（全 / 半角、不可见字符、归一化）。与 <c>--unicode-hygiene</c> 对应。</summary>
    [JsonPropertyName("unicodeHygiene")]
    public bool? UnicodeHygiene { get; set; }

    /// <summary>DateTime 字段趋势 / 周期 / 间隙分析。与 <c>--time-series</c> 对应。</summary>
    [JsonPropertyName("timeSeries")]
    public bool? TimeSeries { get; set; }

    /// <summary>是否生成 P3 分布快照。与 <see cref="DistributionConfig.Snapshot"/> 联动；该值为 true 且 <c>distribution.snapshot</c> 为空时自动填默认路径。</summary>
    [JsonPropertyName("distributionSnapshot")]
    public bool? DistributionSnapshot { get; set; }

    /// <summary>模糊去重目标字段（<c>name</c> / <c>title</c> / <c>address</c> 等）。与 <c>--fuzzy-dedup-field</c> 对应。</summary>
    [JsonPropertyName("fuzzyDedupField")]
    public string? FuzzyDedupField { get; set; }
}
