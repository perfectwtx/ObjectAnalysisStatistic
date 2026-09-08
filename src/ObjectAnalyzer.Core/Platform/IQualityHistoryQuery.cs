namespace ObjectAnalyzer.Platform;

/// <summary>标准化质量历史查询。</summary>
public interface IQualityHistoryQuery
{
    Task<IReadOnlyList<QualitySnapshot>> GetSnapshotsAsync(string? sourceName = null, int limit = 100, CancellationToken cancellationToken = default);
}
