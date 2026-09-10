namespace ObjectAnalyzer.Platform;

/// <summary>一次完整的数据分析作业。</summary>
public sealed class AnalysisJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string SourceName { get; set; } = string.Empty;
    public string SourceType { get; set; } = string.Empty;
    public AnalysisJobStatus Status { get; set; } = AnalysisJobStatus.Pending;
    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAtUtc { get; set; }
    public long DurationMs { get; set; }
    public long TotalObjects { get; set; }
    public int FieldCount { get; set; }
    public double? QualityScore { get; set; }
    public int IssueCount { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ResultJson { get; set; }

    /// <summary>本次作业实际使用的质量门禁结果 JSON。用于历史 Dashboard 保持与执行时一致。</summary>
    public string? GateResultsJson { get; set; }
}
