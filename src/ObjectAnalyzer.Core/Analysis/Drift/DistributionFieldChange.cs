using System.Text.Json.Serialization;

namespace ObjectAnalyzer.Analysis;

/// <summary>覆盖率变化（baseline - current 视为变化量；正数表示覆盖率下降）。</summary>
public sealed class DistributionFieldChange
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("baselineCoverage")] public double BaselineCoverage { get; set; }
    [JsonPropertyName("currentCoverage")] public double CurrentCoverage { get; set; }
    [JsonPropertyName("delta")] public double Delta { get; set; }
}
