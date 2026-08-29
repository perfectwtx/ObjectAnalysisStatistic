using System.Text.Json.Serialization;

namespace ObjectAnalyzer.Analysis;

/// <summary>
/// 数据集级质量期望：对分析结果整体（而非单字段）的约束。
/// </summary>
public class AnalysisExpectations
{
    /// <summary>最少对象行数：分析总行数低于该值时违规。</summary>
    [JsonPropertyName("minRowCount")]
    public long? MinRowCount { get; set; }

    /// <summary>最大重复率（0.0~1.0）：完全相同的重复对象占比超过该值时违规。</summary>
    [JsonPropertyName("maxDuplicateRate")]
    public double? MaxDuplicateRate { get; set; }
}
