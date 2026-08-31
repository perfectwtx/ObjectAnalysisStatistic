using System.Text.Json.Serialization;

namespace ObjectAnalyzer.Analysis;

/// <summary>
/// 分布漂移：每字段 t-digest + HLL 草图导出、与历史基线做漂移对比、低可信阈值。
/// </summary>
public class DistributionConfig
{
    /// <summary>导出分布快照到该路径。与 <c>--export-distribution-snapshot</c> 对应。</summary>
    [JsonPropertyName("snapshot")]
    public string? Snapshot { get; set; }

    /// <summary>作为漂移基线的历史分布快照路径。与 <c>--diff-distribution</c> 对应。</summary>
    [JsonPropertyName("diffBaseline")]
    public string? DiffBaseline { get; set; }

    /// <summary>分布漂移结果报告输出路径。与 <c>--distribution-diff-report</c> 对应。</summary>
    [JsonPropertyName("diffReport")]
    public string? DiffReport { get; set; }

    /// <summary>采样置信区间宽度低于此值判为"低可信"（仅库 API 暴露，CLI 不直接对应）。</summary>
    [JsonPropertyName("lowConfidenceWidth")]
    public double? LowConfidenceWidth { get; set; }
}
