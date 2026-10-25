namespace ObjectAnalyzer.Platform;

/// <summary>标准化质量问题查询。</summary>
public interface IQualityIssueQuery
{
    Task<QualityIssueSearchPage> SearchAsync(QualityIssueSearch search, CancellationToken cancellationToken = default);
    Task<QualityIssueSummary> SummarizeAsync(QualityIssueSearch search, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<QualityIssue>> GetByJobAsync(Guid jobId, CancellationToken cancellationToken = default);
}
