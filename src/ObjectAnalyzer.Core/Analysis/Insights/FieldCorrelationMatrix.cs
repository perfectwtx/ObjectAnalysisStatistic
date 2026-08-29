using System.Text.Json.Serialization;

namespace ObjectAnalyzer.Analysis;

/// <summary>
/// 字段间相关性矩阵：两两计算 Pearson 线性相关系数与 Spearman 秩相关系数。
/// 设计取舍：相关性需要"同一对象的多个字段在同一时刻的取值"——无法仅凭字段级预聚合统计反推，
/// 因此分析阶段需为每个对象按字段抽取数值形成"行样本"（最多保留 N 行以控内存）。
/// </summary>
public sealed class FieldCorrelationMatrix
{
    /// <summary>参与相关性计算的字段名（顺序与矩阵行列一致）。</summary>
    [JsonPropertyName("fields")]
    public List<string> Fields { get; set; } = new();

    /// <summary>行样本数（用于相关性估算的样本数）。</summary>
    [JsonPropertyName("samples")]
    public long Samples { get; set; }

    /// <summary>每对字段实际参与计算的样本数（行内两个字段均非空的行数）。</summary>
    [JsonPropertyName("pairwiseSampleCounts")]
    public List<List<long>> PairwiseSampleCounts { get; set; } = new();

    /// <summary>字段对的 Pearson 系数矩阵（行 = i，列 = j，与 fields 同序）；null 表示样本不足。</summary>
    [JsonPropertyName("pearson")]
    public List<List<double?>> Pearson { get; set; } = new();

    /// <summary>字段对的 Spearman 秩系数矩阵。</summary>
    [JsonPropertyName("spearman")]
    public List<List<double?>> Spearman { get; set; } = new();

    /// <summary>
    /// 显著相关性对：|Pearson| ≥ 阈值（默认 0.7）且样本数 ≥ 30 时的强相关字段对，
    /// 便于快速浏览（无需查看完整矩阵）。
    /// </summary>
    [JsonPropertyName("strongPairs")]
    public List<StrongCorrelationPair> StrongPairs { get; set; } = new();
}
