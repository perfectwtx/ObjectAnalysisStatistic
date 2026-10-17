using System.Text.Json.Serialization;

namespace ObjectAnalyzer.Analysis;

/// <summary>
/// 分布快照：每字段保留足以做"分布漂移"对比的草图与摘要。
/// 设计目标：
///  - JSON 可序列化：t-digest 用质心数组、HLL 用稠密寄存器的字节序列
///  - 内存有界：每字段 ~10KB 以内（256 质心 + 16K 寄存器）
///  - 与 SchemaSnapshot 互补：SchemaSnapshot 描述"结构"，DistributionSnapshot 描述"分布"
/// 适用场景：
///  - null 率从 1% 升到 20%（用 NullRateBefore/After 直接可见）
///  - 均值偏移 1σ（用 MeanBefore/After + StdDev 对比）
///  - 枚举字段出现新值（用 HyperLogLog 估计 distinct count + Jaccard 对比）
///  - 数值分布整体迁移（用 t-digest 多分位对比）
/// </summary>
public sealed class DistributionSnapshot
{
    /// <summary>快照格式版本。</summary>
    [JsonPropertyName("snapshotVersion")]
    public string SnapshotVersion { get; set; } = "2.0";

    /// <summary>生成时间（UTC）。</summary>
    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>来源标识（输入文件路径 / db://provider?query）。</summary>
    [JsonPropertyName("source")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Source { get; set; }

    /// <summary>被分析对象总数。</summary>
    [JsonPropertyName("totalObjects")]
    public long TotalObjects { get; set; }

    /// <summary>字段分布列表。</summary>
    [JsonPropertyName("fields")]
    public List<DistributionSnapshotField> Fields { get; set; } = new();
}
