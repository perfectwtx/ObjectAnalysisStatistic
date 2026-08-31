using System.Text.Json.Serialization;

namespace ObjectAnalyzer.Analysis;

/// <summary>
/// PII 与合规等级：全局关闭 PII 识别、合规报告导出、CI 门禁等级。
/// </summary>
public class PiiRuntimeConfig
{
    /// <summary>全局关闭 PII 识别与打码（输出原始值）。与 <c>--pii-off</c> 对应。</summary>
    [JsonPropertyName("off")]
    public bool? Off { get; set; }

    /// <summary>导出合规等级报告到该路径。与 <c>--pii-report</c> 对应。</summary>
    [JsonPropertyName("report")]
    public string? Report { get; set; }

    /// <summary>CI 合规门禁等级：A / B / C。null = 不门禁。与 <c>--fail-on-pii-level</c> 对应。</summary>
    [JsonPropertyName("failOnLevel")]
    public string? FailOnLevel { get; set; }
}
