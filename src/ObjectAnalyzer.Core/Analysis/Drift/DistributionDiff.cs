using System.Text.Json.Serialization;

namespace ObjectAnalyzer.Analysis;

/// <summary>
/// 分布差异：对比两份 <see cref="DistributionSnapshot"/>，
/// 分类：null 率变化、均值偏移、分位迁移、distinct 漂移。
/// </summary>
public sealed class DistributionDiff
{
    [JsonPropertyName("diffVersion")] public string DiffVersion { get; set; } = "1.0";
    [JsonPropertyName("generatedAt")] public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;
    [JsonPropertyName("baselineSource")] public string? BaselineSource { get; set; }
    [JsonPropertyName("currentSource")] public string? CurrentSource { get; set; }

    /// <summary>覆盖率变化列表（基线 - 当前，按 |Δcoverage| 降序）。</summary>
    [JsonPropertyName("coverageChanges")]
    public List<DistributionFieldChange> CoverageChanges { get; set; } = new();

    /// <summary>数值分布漂移：均值 / 中位数 / P95 偏移列表。</summary>
    [JsonPropertyName("numericDrifts")]
    public List<NumericDrift> NumericDrifts { get; set; } = new();

    /// <summary>distinct 漂移：基线 vs 当前的不同值集 Jaccard + 计数变化。</summary>
    [JsonPropertyName("distinctDrifts")]
    public List<DistinctDrift> DistinctDrifts { get; set; } = new();

    /// <summary>新出现的字段 / 已消失字段（与 SchemaDiff 类似但口径仅看"出现过的字段"）。</summary>
    [JsonPropertyName("addedFields")]
    public List<DistributionSnapshotField> AddedFields { get; set; } = new();
    [JsonPropertyName("removedFields")]
    public List<DistributionSnapshotField> RemovedFields { get; set; } = new();

    /// <summary>显著漂移的字段总数（coverage Δ ≥ 0.1 或 |mean shift| ≥ 0.5σ）。</summary>
    [JsonPropertyName("significantDriftCount")]
    public int SignificantDriftCount { get; set; }
}
