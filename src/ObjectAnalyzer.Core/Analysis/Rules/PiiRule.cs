using System.Text.Json.Serialization;

namespace ObjectAnalyzer.Analysis;

/// <summary>
/// 自定义 PII 规则：声明业务敏感字段及其打码格式。
/// 示例：
/// <code>
/// "fields": {
///   "internalId": {
///     "pii": { "label": "内部标识", "highSeverity": false, "mask": { "keepPrefix": 2, "keepSuffix": 2 } }
///   },
///   "authToken": { "pii": { "mask": { "maskAll": true } } }
/// }
/// </code>
/// </summary>
public class PiiRule
{
    /// <summary>敏感分类标签，用于合规报告展示（如 "内部标识"）。缺省为 "业务敏感字段"。</summary>
    [JsonPropertyName("label")]
    public string? Label { get; set; }

    /// <summary>是否属于高敏感（默认 true），影响合规报告中的字段排序。</summary>
    [JsonPropertyName("highSeverity")]
    public bool HighSeverity { get; set; } = true;

    /// <summary>打码格式；缺省时使用全量打码（maskAll=true）。</summary>
    [JsonPropertyName("mask")]
    public PiiMaskConfig? Mask { get; set; }
}
