using System.Text.Json.Serialization;

namespace ObjectAnalyzer.Analysis;

/// <summary>t-digest 单个质心（均值 + 权重）。</summary>
public sealed class QuantileCentroid
{
    [JsonPropertyName("mean")] public double Mean { get; set; }
    [JsonPropertyName("weight")] public long Weight { get; set; }
}
