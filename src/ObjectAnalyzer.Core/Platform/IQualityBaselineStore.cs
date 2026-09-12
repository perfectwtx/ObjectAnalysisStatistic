namespace ObjectAnalyzer.Platform;

/// <summary>数据质量基线持久化抽象。</summary>
public interface IQualityBaselineStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<QualityBaseline?> GetAsync(string sourceName, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<QualityBaseline>> GetAllAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(QualityBaseline baseline, CancellationToken cancellationToken = default);
    Task DeleteAsync(string sourceName, CancellationToken cancellationToken = default);
}
