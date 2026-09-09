namespace ObjectAnalyzer.Platform;

/// <summary>质量告警规则持久化抽象。</summary>
public interface IAlertRuleStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AlertRule>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<AlertRule?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task SaveAsync(AlertRule rule, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
