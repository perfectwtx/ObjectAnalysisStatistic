using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace ObjectAnalyzer.Analysis;

/// <summary>
/// 时间序列深度分析：基于已聚合的 DateHistogram（含跨度自适应切档的桶）。
/// 输出：
///  - Trend：单调性（升 / 降 / 平稳）
///  - Periodicity：周期检测（自相关 + 简单周期启发式）
///  - GapAnalysis：相邻桶间最大间隔 / 间隙数 / 间隙占比
///  - Burstiness：脉冲性（峰值倍率 / 活跃度离散度）
/// </summary>
public sealed class TimeSeriesAnalysis
{
    /// <summary>时间序列起点（与 <see cref="End"/> 一起确定时间窗）。</summary>
    [JsonPropertyName("start")]
    public DateTimeOffset Start { get; set; }

    /// <summary>时间序列终点。</summary>
    [JsonPropertyName("end")]
    public DateTimeOffset End { get; set; }

    /// <summary>单桶跨度（与 <see cref="FieldStatistic.DateHistogram"/> 的桶尺寸一致）。</summary>
    [JsonPropertyName("bucketSpan")]
    public TimeSpan BucketSpan { get; set; }

    /// <summary>桶总数（含空桶）。</summary>
    [JsonPropertyName("bucketCount")]
    public int BucketCount { get; set; }

    /// <summary>总样本数（与 DateTimeCount 一致）。</summary>
    [JsonPropertyName("totalSamples")]
    public long TotalSamples { get; set; }

    /// <summary>趋势判定："rising" / "falling" / "stable" / "insufficient"。</summary>
    [JsonPropertyName("trend")]
    public string Trend { get; set; } = "insufficient";

    /// <summary>趋势斜率（普通最小二乘法；单位：样本数 / 桶）。</summary>
    [JsonPropertyName("trendSlope")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? TrendSlope { get; set; }

    /// <summary>Theil-Sen 斜率（对异常值稳健；样本数 ≥ 10 时计算）。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? TheilSenSlope { get; set; }

    /// <summary>拟合优度 R²（普通最小二乘）。0~1，越高表示线性趋势越强。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? TrendRSquared { get; set; }

    /// <summary>检测到的周期（null 表示未检测到稳定周期）。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DetectedPeriod { get; set; }

    /// <summary>周期自相关峰值（0~1）。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? PeriodicityStrength { get; set; }

    /// <summary>最大相邻桶间隙（以桶数计）。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? LongestGapBuckets { get; set; }

    /// <summary>空桶占比（0~1）。</summary>
    [JsonPropertyName("emptyRatio")]
    public double EmptyRatio { get; set; }

    /// <summary>脉冲性指标：峰值 / 中位数（≥ 3 视为脉冲型序列）。</summary>
    [JsonPropertyName("burstiness")]
    public double Burstiness { get; set; }

    /// <summary>每个桶的样本数（按时序对齐；同 DateHistogram）。</summary>
    [JsonPropertyName("series")]
    public List<TimeSeriesBucket> Series { get; set; } = new();
}
