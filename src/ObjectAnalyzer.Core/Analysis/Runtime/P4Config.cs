using System.Text.Json.Serialization;

namespace ObjectAnalyzer.Analysis;

/// <summary>
/// P4 深度分析开关集合（默认全关，开启会增加运行时间与内存）。
/// <para>P4 开关<strong>仅在规则文件 <c>runtime.p4</c> 与 API 端</strong>暴露（API 形参为 <c>enableCorrelation</c> 等 camelCase 形式），CLI 不直接对应——CLI 用户请把 P4 开关写进规则文件并通过 <c>-r rules.json</c> 加载。</para>
/// </summary>
public class P4Config
{
    /// <summary>Pearson + Spearman 相关性矩阵。API: <c>enableCorrelation</c>。</summary>
    [JsonPropertyName("correlation")]
    public bool? Correlation { get; set; }

    /// <summary>字符串形态模板归纳（如 <c>###-####</c>）。API: <c>enableStringPatterns</c>。</summary>
    [JsonPropertyName("stringPatterns")]
    public bool? StringPatterns { get; set; }

    /// <summary>Unicode 卫生检查（全 / 半角、不可见字符、归一化）。API: <c>enableUnicodeHygiene</c>。</summary>
    [JsonPropertyName("unicodeHygiene")]
    public bool? UnicodeHygiene { get; set; }

    /// <summary>DateTime 字段趋势 / 周期 / 间隙分析。API: <c>enableTimeSeries</c>。</summary>
    [JsonPropertyName("timeSeries")]
    public bool? TimeSeries { get; set; }

    /// <summary>是否生成 P3 分布快照（自动填 <see cref="DistributionConfig.Snapshot"/> 默认路径）。API: <c>enableDistributionSnapshot</c>。</summary>
    [JsonPropertyName("distributionSnapshot")]
    public bool? DistributionSnapshot { get; set; }

    /// <summary>模糊去重目标字段（<c>name</c> / <c>title</c> / <c>address</c> 等）。仅规则文件 / API 暴露。</summary>
    [JsonPropertyName("fuzzyDedupField")]
    public string? FuzzyDedupField { get; set; }
}
