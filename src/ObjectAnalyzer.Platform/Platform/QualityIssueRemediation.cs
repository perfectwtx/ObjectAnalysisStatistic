namespace ObjectAnalyzer.Platform;

/// <summary>质量问题整改记录。</summary>
public sealed record QualityIssueRemediation(
    Guid Id,
    Guid JobId,
    string FieldName,
    string Check,
    string Action,
    string Status = "Planned",
    DateTime? DueAtUtc = null,
    DateTime? CompletedAtUtc = null,
    string? Comment = null)
{
    public DateTime CreatedAtUtc { get; init; }

    public void Validate()
    {
        if (Id == Guid.Empty)
            throw new ArgumentException("整改记录 ID 不能为空。", nameof(Id));
        if (JobId == Guid.Empty)
            throw new ArgumentException("分析任务 ID 不能为空。", nameof(JobId));
        if (string.IsNullOrWhiteSpace(FieldName))
            throw new ArgumentException("字段名称不能为空。", nameof(FieldName));
        if (string.IsNullOrWhiteSpace(Check))
            throw new ArgumentException("检查类型不能为空。", nameof(Check));
        if (string.IsNullOrWhiteSpace(Action))
            throw new ArgumentException("整改措施不能为空。", nameof(Action));
        if (string.IsNullOrWhiteSpace(Status))
            throw new ArgumentException("整改状态不能为空。", nameof(Status));
    }
}
