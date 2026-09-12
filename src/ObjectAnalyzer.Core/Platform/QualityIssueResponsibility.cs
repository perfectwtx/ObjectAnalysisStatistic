namespace ObjectAnalyzer.Platform;

/// <summary>质量问题责任与处理状态。</summary>
public sealed record QualityIssueResponsibility(
    Guid JobId,
    string FieldName,
    string Check,
    string? Owner = null,
    string Status = "Open",
    string? Comment = null,
    DateTime? UpdatedAtUtc = null)
{
    public void Validate()
    {
        if (JobId == Guid.Empty)
            throw new ArgumentException("分析任务 ID 不能为空。", nameof(JobId));
        if (string.IsNullOrWhiteSpace(FieldName))
            throw new ArgumentException("字段名称不能为空。", nameof(FieldName));
        if (string.IsNullOrWhiteSpace(Check))
            throw new ArgumentException("检查类型不能为空。", nameof(Check));
        if (string.IsNullOrWhiteSpace(Status))
            throw new ArgumentException("处理状态不能为空。", nameof(Status));
    }
}
