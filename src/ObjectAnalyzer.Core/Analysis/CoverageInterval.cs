using System.Text.Json.Serialization;

namespace ObjectAnalyzer.Analysis;

/// <summary>单字段覆盖率 Wilson 95% 置信区间。</summary>
public sealed class CoverageInterval
{
    /// <summary>采样观察到的覆盖率（点估计）。</summary>
    [JsonPropertyName("observed")]
    public double Observed { get; set; }

    /// <summary>区间下界。</summary>
    [JsonPropertyName("lower")]
    public double Lower { get; set; }

    /// <summary>区间上界。</summary>
    [JsonPropertyName("upper")]
    public double Upper { get; set; }

    /// <summary>区间宽度（upper − lower）。</summary>
    [JsonPropertyName("width")]
    public double Width { get; set; }

    /// <summary>区间下界为 0 时为 true（"完全没观察到" 含义模糊：可能是真没出现，也可能是采样运气差）。</summary>
    [JsonPropertyName("ambiguous")]
    public bool Ambiguous { get; set; }
}
