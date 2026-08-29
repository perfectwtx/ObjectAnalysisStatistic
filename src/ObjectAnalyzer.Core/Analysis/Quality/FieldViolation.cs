namespace ObjectAnalyzer.Analysis;

/// <summary>
/// 单条质量违规。字段级检查 Field 为字段名；数据集级期望（rowCount/duplicateRate）Field 为空串。
/// Count 为违规出现次数（行数/值次数；枚举聚合条目为不同违规值个数）。
/// </summary>
public sealed record FieldViolation
{
    public string Field { get; init; } = "";
    public string Check { get; init; } = "";
    public long Count { get; init; }
    public string Message { get; init; } = "";

    /// <summary>计数是否为近似值（唯一性检查在唯一值截断时经 HyperLogLog 估计）。</summary>
    public bool Approximate { get; init; }

    /// <summary>违规样本（最多 {MaxSamples} 个；数据集级检查为空）。</summary>
    public IReadOnlyList<string> Samples { get; init; } = Array.Empty<string>();
}
