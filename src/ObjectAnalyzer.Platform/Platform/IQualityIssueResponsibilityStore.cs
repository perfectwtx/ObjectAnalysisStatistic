namespace ObjectAnalyzer.Platform;

/// <summary>质量问题责任与处理状态持久化抽象。</summary>
public interface IQualityIssueResponsibilityStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<QualityIssueResponsibility>> GetByJobAsync(Guid jobId, CancellationToken cancellationToken = default);
    Task<QualityIssueResponsibility?> GetAsync(Guid jobId, string fieldName, string check, CancellationToken cancellationToken = default);
    Task SaveAsync(QualityIssueResponsibility responsibility, CancellationToken cancellationToken = default);
}
