using System.Text.Json.Serialization;

namespace ObjectAnalyzer.Analysis;

/// <summary>distinct 漂移：Jaccard（值集合相似度）+ 计数变化。</summary>
public sealed class DistinctDrift
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? BaselineDistinct { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? CurrentDistinct { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? DeltaRatio { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Jaccard { get; set; }

    [JsonPropertyName("severity")]
    public string Severity { get; set; } = "info";
}
