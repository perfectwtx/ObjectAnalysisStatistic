using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;

namespace ObjectAnalyzer.Analysis;

/// <summary>单字段分布快照。</summary>
public sealed class DistributionSnapshotField
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";

    /// <summary>主导类型（Number / String / DateTime / Boolean）。</summary>
    [JsonPropertyName("primaryType")] public string PrimaryType { get; set; } = "Unknown";

    /// <summary>覆盖率（字段在对象中出现的比例）。</summary>
    [JsonPropertyName("coverage")] public double Coverage { get; set; }

    /// <summary>出现次数（Count，非样本数）。</summary>
    [JsonPropertyName("count")] public long Count { get; set; }

    // ── 数值字段 ──
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? MeanNumber { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? StdDevNumber { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? MinNumber { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? MaxNumber { get; set; }

    /// <summary>t-digest 质心序列：[(mean, weight), ...]。可重建 t-digest 用于多分位对比。</summary>
    [JsonPropertyName("quantileCentroids")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<QuantileCentroid>? QuantileCentroids { get; set; }

    // ── 字符串字段 ──
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MinLength { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxLength { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? AvgLength { get; set; }

    /// <summary>HyperLogLog 稠密寄存器（base64 编码）；可重建 HLL 用于 distinct 对比。</summary>
    [JsonPropertyName("distinctHllRegisters")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DistinctHllRegisters { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DistinctHllPrecision { get; set; }

    /// <summary>HyperLogLog 基数估计（快照时记录，避免重建）。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? DistinctCount { get; set; }
}
