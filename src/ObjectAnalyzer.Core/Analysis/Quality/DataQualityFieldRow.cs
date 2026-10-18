namespace ObjectAnalyzer.Analysis;

/// <summary>数据质量报告中单个字段的质量明细。</summary>
public sealed class DataQualityFieldRow
{
    public string Field { get; init; } = "";
    public long Count { get; init; }
    public double Coverage { get; init; }
    public string PrimaryType { get; init; } = "";
    public bool HasRule { get; init; }
    public bool HasDefaultValue { get; init; }
    public long ViolationCount { get; init; }
    public IReadOnlyList<string> Violations { get; init; } = Array.Empty<string>();

    /// <summary>质量检查 v2 条目（范围/模式/必填/唯一/null 率）；枚举违规仍走 Violations 列表。</summary>
    public IReadOnlyList<FieldViolation> Checks { get; init; } = Array.Empty<FieldViolation>();
}
