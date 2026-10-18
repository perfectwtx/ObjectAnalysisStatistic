namespace ObjectAnalyzer.Platform;

/// <summary>一次成功分析的标准化质量快照。</summary>
public sealed record QualitySnapshot(
    Guid JobId,
    string SourceName,
    DateTime OccurredAtUtc,
    long TotalObjects,
    int FieldCount,
    int IssueCount,
    QualityMetrics Metrics);

/// <summary>字段级质量快照，用于字段质量趋势和排名。</summary>
public sealed record FieldQualitySnapshot(
    Guid JobId,
    string FieldName,
    double Coverage,
    double NullRate,
    double? Uniqueness,
    double? OutlierRatio);

/// <summary>标准化质量问题记录。</summary>
public sealed record QualityIssue(
    Guid JobId,
    string FieldName,
    string Check,
    long Count,
    string Message);
