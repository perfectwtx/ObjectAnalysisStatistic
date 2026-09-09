using ObjectAnalyzer.Platform;

namespace ObjectAnalyzer.Tests.Platform;

public sealed class AnalysisExecutionCoordinatorTests
{
    [Fact]
    public async Task ExecuteAsync_WhenCancelled_PersistsCancelledJob()
    {
        var history = new FakeHistoryStore();
        var alerts = new FakeAlertRuleStore();
        var deliveries = new FakeWebhookDeliveryStore();
        var service = new AnalysisJobService(history, alerts, deliveries);
        var coordinator = new AnalysisExecutionCoordinator(service);
        using var cts = new CancellationTokenSource();

        var task = coordinator.ExecuteAsync(
            "unit-test",
            "json",
            async token =>
            {
                cts.Cancel();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("unreachable");
            },
            cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);

        var job = Assert.Single(history.Jobs);
        Assert.Equal(AnalysisJobStatus.Cancelled, job.Status);
        Assert.NotNull(job.CompletedAtUtc);
        Assert.Equal("分析已取消。", job.ErrorMessage);
    }

    private sealed class FakeHistoryStore : IAnalysisHistoryStore
    {
        public List<AnalysisJob> Jobs { get; } = [];
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SaveAsync(AnalysisJob job, CancellationToken cancellationToken = default)
        {
            var existing = Jobs.FindIndex(x => x.Id == job.Id);
            if (existing >= 0) Jobs[existing] = job;
            else Jobs.Add(job);
            return Task.CompletedTask;
        }

        public Task SaveQualitySnapshotAsync(QualitySnapshot snapshot, IReadOnlyList<FieldQualitySnapshot> fields, IReadOnlyList<QualityIssue> issues, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<AnalysisJob?> GetAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.FromResult(Jobs.FirstOrDefault(x => x.Id == id));

        public Task<IReadOnlyList<AnalysisJob>> GetRecentAsync(int limit = 50, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AnalysisJob>>(Jobs.OrderByDescending(x => x.StartedAtUtc).Take(limit).ToList());
    }

    private sealed class FakeAlertRuleStore : IAlertRuleStore
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<AlertRule>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AlertRule>>([]);
        public Task<AlertRule?> GetAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<AlertRule?>(null);
        public Task SaveAsync(AlertRule rule, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeWebhookDeliveryStore : IWebhookDeliveryStore
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task EnqueueAsync(WebhookDelivery delivery, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<WebhookDelivery?> GetNextPendingAsync(DateTime utcNow, CancellationToken cancellationToken = default) => Task.FromResult<WebhookDelivery?>(null);
        public Task MarkProcessingAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task MarkSucceededAsync(Guid id, DateTime sentAtUtc, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task MarkFailedAsync(Guid id, string errorMessage, DateTime nextAttemptAtUtc, bool permanent, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
