using System.Text.Json.Serialization;

namespace ObjectAnalyzer.Analysis;

/// <summary>
/// P6 增量分析：检查点落盘、断点续跑、基线合并。
/// </summary>
public class CheckpointConfig
{
    /// <summary>分析过程中定期落盘检查点（每 10K 对象）。与 <c>--checkpoint</c> 对应。</summary>
    [JsonPropertyName("path")]
    public string? Path { get; set; }

    /// <summary>从历史检查点续跑（位置 + 累计结果）。与 <c>--resume</c> 对应。</summary>
    [JsonPropertyName("resumeFrom")]
    public string? ResumeFrom { get; set; }

    /// <summary>与历史 <c>AnalysisResult</c> JSON 合并。与 <c>--baseline</c> 对应。</summary>
    [JsonPropertyName("baseline")]
    public string? Baseline { get; set; }
}
