namespace ObjectAnalyzer.Platform;

/// <summary>Webhook Outbox 持久化抽象。</summary>
public interface IWebhookDeliveryStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task EnqueueAsync(WebhookDelivery delivery, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WebhookDelivery>> GetRecentAsync(int limit = 100, CancellationToken cancellationToken = default);
    Task<WebhookDelivery?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<bool> RetryAsync(Guid id, CancellationToken cancellationToken = default);
    /// <summary>Atomically claims a due delivery until its lease expires.</summary>
    Task<WebhookDelivery?> TryClaimNextAsync(DateTime utcNow, TimeSpan leaseDuration, CancellationToken cancellationToken = default);
    Task<bool> MarkSucceededAsync(Guid id, string leaseToken, DateTime sentAtUtc, CancellationToken cancellationToken = default);
    Task<bool> MarkFailedAsync(Guid id, string leaseToken, string errorMessage, DateTime nextAttemptAtUtc, bool permanent, CancellationToken cancellationToken = default);
}
