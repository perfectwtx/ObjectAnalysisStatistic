namespace ObjectAnalyzer.Platform;

public interface IAnalysisHistoryStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(AnalysisJob job, CancellationToken cancellationToken = default);

    /// <summary>仅当数据库中的作业仍处于指定状态时，原子地写入新的作业状态。</summary>
    Task<bool> TryTransitionAsync(AnalysisJob job, AnalysisJobStatus expectedStatus, CancellationToken cancellationToken = default);
    Task SaveQualitySnapshotAsync(QualitySnapshot snapshot, IReadOnlyList<FieldQualitySnapshot> fields, IReadOnlyList<QualityIssue> issues, CancellationToken cancellationToken = default);
    Task<AnalysisJob?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AnalysisJob>> GetRecentAsync(int limit = 50, CancellationToken cancellationToken = default);

    /// <summary>
    /// 删除指定分析作业及其关联的质量快照、字段质量、问题、责任、整改与 webhook 投递记录。
    /// </summary>
    /// <returns>若存在并已删除返回 true；不存在返回 false。</returns>
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
