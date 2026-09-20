namespace ObjectAnalyzer.Platform;

/// <summary>Webhook Outbox 持久化抽象。</summary>
public interface IWebhookDeliveryStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task EnqueueAsync(WebhookDelivery delivery, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WebhookDelivery>> GetRecentAsync(int limit = 100, CancellationToken cancellationToken = default);
    Task<WebhookDelivery?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<bool> RetryAsync(Guid id, CancellationToken cancellationToken = default);
    Task<WebhookDelivery?> GetNextPendingAsync(DateTime utcNow, CancellationToken cancellationToken = default);
    Task MarkProcessingAsync(Guid id, CancellationToken cancellationToken = default);
    Task MarkSucceededAsync(Guid id, DateTime sentAtUtc, CancellationToken cancellationToken = default);
    Task MarkFailedAsync(Guid id, string errorMessage, DateTime nextAttemptAtUtc, bool permanent, CancellationToken cancellationToken = default);
}
