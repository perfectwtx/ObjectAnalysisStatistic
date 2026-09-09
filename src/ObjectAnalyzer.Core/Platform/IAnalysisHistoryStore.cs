namespace ObjectAnalyzer.Platform;

public interface IAnalysisHistoryStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(AnalysisJob job, CancellationToken cancellationToken = default);
    Task SaveQualitySnapshotAsync(QualitySnapshot snapshot, IReadOnlyList<FieldQualitySnapshot> fields, IReadOnlyList<QualityIssue> issues, CancellationToken cancellationToken = default);
    Task<AnalysisJob?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AnalysisJob>> GetRecentAsync(int limit = 50, CancellationToken cancellationToken = default);
}
