namespace ObjectAnalyzer.Platform;

/// <summary>质量问题整改记录持久化接口。</summary>
public interface IQualityIssueRemediationStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<QualityIssueRemediation>> GetByIssueAsync(
        Guid jobId,
        string fieldName,
        string check,
        CancellationToken cancellationToken = default);
    Task SaveAsync(QualityIssueRemediation remediation, CancellationToken cancellationToken = default);
}
