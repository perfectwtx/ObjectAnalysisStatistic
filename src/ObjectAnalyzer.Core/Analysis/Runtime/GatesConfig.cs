using System.Text.Json.Serialization;

namespace ObjectAnalyzer.Analysis;

/// <summary>
/// CI 质量门禁：违规即失败、异常值比例门禁。
/// </summary>
public class GatesConfig
{
    /// <summary>存在任一违规即以退出码 3 结束。与 <c>--fail-on-violations</c> 对应。</summary>
    [JsonPropertyName("failOnViolations")]
    public bool? FailOnViolations { get; set; }

    /// <summary>任一数值字段的 IQR 异常值比例超过该阈值（0.0~1.0）即以退出码 5 结束。null = 不门禁。与 <c>--fail-on-outlier-ratio</c> 对应。</summary>
    [JsonPropertyName("failOnOutlierRatio")]
    public double? FailOnOutlierRatio { get; set; }
}
