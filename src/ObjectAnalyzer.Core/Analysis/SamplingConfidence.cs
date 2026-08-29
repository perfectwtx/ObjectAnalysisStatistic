using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ObjectAnalyzer.Analysis;

/// <summary>
/// 采样不确定性 / 置信区间结果：对启用 sampling 时的统计可信度做量化。
/// 经典 Wilsonscore 95% 置信区间：低覆盖字段的可信度可据此判定（覆盖率 0% 同样有意义 —— 区间上界可能仅 5%）。
/// </summary>
public sealed class SamplingConfidence
{
    /// <summary>采样方式："reservoir" / "systematic" / null（全量分析时不填充）。</summary>
    [JsonPropertyName("samplingMethod")]
    public string? SamplingMethod { get; set; }

    /// <summary>有效样本数（reservoir=容量，systematic=总行/step）。</summary>
    [JsonPropertyName("effectiveSampleSize")]
    public long EffectiveSampleSize { get; set; }

    /// <summary>原始记录数（sampling 之前估算的输入总行数）。</summary>
    [JsonPropertyName("estimatedTotal")]
    public long EstimatedTotal { get; set; }

    /// <summary>采样比例（0~1，reservoir=sample/total，systematic=1/step）。</summary>
    [JsonPropertyName("samplingFraction")]
    public double SamplingFraction { get; set; }

    /// <summary>95% 置信水平（用于 Wilson 区间 / 比例误差）。</summary>
    [JsonPropertyName("confidenceLevel")]
    public double ConfidenceLevel { get; set; } = 0.95;

    /// <summary>各字段的覆盖率 95% 置信区间。key = 字段名，value = (lo, hi)。</summary>
    [JsonPropertyName("coverageIntervals")]
    public Dictionary<string, CoverageInterval> CoverageIntervals { get; set; } = new();

    /// <summary>覆盖率低可信的字段（区间宽度 ≥ 0.2 或样本数为 0）。</summary>
    [JsonPropertyName("lowConfidenceFields")]
    public List<string> LowConfidenceFields { get; set; } = new();

    /// <summary>采样方法的整体描述（供人类阅读）。</summary>
    [JsonPropertyName("summary")]
    public string Summary { get; set; } = "";
}
