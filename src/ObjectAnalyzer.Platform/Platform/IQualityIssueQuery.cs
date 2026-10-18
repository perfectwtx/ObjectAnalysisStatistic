namespace ObjectAnalyzer.Platform;

/// <summary>标准化质量问题查询。</summary>
public interface IQualityIssueQuery
{
    Task<IReadOnlyList<QualityIssue>> GetByJobAsync(Guid jobId, CancellationToken cancellationToken = default);
}
