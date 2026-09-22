using ObjectAnalyzer.Platform;
using ObjectAnalyzer.Analysis;

namespace ObjectAnalyzer.Tests.Platform;

public sealed class AnalysisExecutionCoordinatorTests
{
    [Fact]
    public async Task RecoverOrphanedJobsAsync_MarksPendingAndRunningJobsAsFailed()
    {
        var history = new FakeHistoryStore();
        var alerts = new FakeAlertRuleStore();
        var deliveries = new FakeWebhookDeliveryStore();
        var service = new AnalysisJobService(history, alerts, deliveries);

        await history.SaveAsync(new AnalysisJob { Id = Guid.NewGuid(), Status = AnalysisJobStatus.Pending, StartedAtUtc = DateTime.UtcNow.AddMinutes(-3) });
        await history.SaveAsync(new AnalysisJob { Id = Guid.NewGuid(), Status = AnalysisJobStatus.Running, StartedAtUtc = DateTime.UtcNow.AddMinutes(-2) });
        await history.SaveAsync(new AnalysisJob { Id = Guid.NewGuid(), Status = AnalysisJobStatus.Succeeded });

        var recovered = await service.RecoverOrphanedJobsAsync();

        Assert.Equal(2, recovered);
        Assert.Equal(2, history.Jobs.Count(x => x.Status == AnalysisJobStatus.Failed));
        Assert.All(history.Jobs.Where(x => x.Status == AnalysisJobStatus.Failed), job =>
        {
            Assert.NotNull(job.CompletedAtUtc);
            Assert.Equal("应用重启后检测到未完成的分析作业，已标记为失败。", job.ErrorMessage);
        });
        Assert.Contains(history.Jobs, x => x.Status == AnalysisJobStatus.Succeeded);
    }

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
                return CreateMinimalResult();
            },
            null,
            cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);

        var job = Assert.Single(history.Jobs);
        Assert.Equal(AnalysisJobStatus.Cancelled, job.Status);
        Assert.NotNull(job.CompletedAtUtc);
        Assert.Equal("分析已取消。", job.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteAsync_WhenCancellationIsRequestedBeforeFailure_PersistsCancelledJob()
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
            _ =>
            {
                // 模拟取消已经发生，但底层执行器随后抛出普通异常。
                cts.Cancel();
                throw new InvalidOperationException("late failure");
            },
            null,
            cts.Token);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => task);

        Assert.Equal("late failure", exception.Message);
        var job = Assert.Single(history.Jobs);
        Assert.Equal(AnalysisJobStatus.Cancelled, job.Status);
        Assert.NotNull(job.CompletedAtUtc);
        Assert.Equal("分析已取消。", job.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteAsync_WhenCancellationIsRequestedAfterExecution_PersistsCancelledJob()
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
            token =>
            {
                // 模拟底层执行器没有及时观察取消信号，但返回了结果。
                cts.Cancel();
                return Task.FromResult(CreateMinimalResult());
            },
            null,
            cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);

        var job = Assert.Single(history.Jobs);
        Assert.Equal(AnalysisJobStatus.Cancelled, job.Status);
        Assert.NotNull(job.CompletedAtUtc);
    }

    [Fact]
    public async Task CompleteAsync_WhenJobWasAlreadyCancelled_DoesNotOverwriteCancelledState()
    {
        var history = new FakeHistoryStore();
        var alerts = new FakeAlertRuleStore();
        var deliveries = new FakeWebhookDeliveryStore();
        var service = new AnalysisJobService(history, alerts, deliveries);
        var job = await service.StartAsync(Guid.NewGuid(), "unit-test", "json");

        await service.CancelAsync(job);
        await service.CompleteAsync(job, CreateMinimalResult());

        var persisted = Assert.Single(history.Jobs);
        Assert.Equal(AnalysisJobStatus.Cancelled, persisted.Status);
        Assert.Equal("分析已取消。", persisted.ErrorMessage);
        Assert.Null(persisted.ResultJson);
    }

    [Fact]
    public async Task FailAsync_WhenJobWasAlreadyCancelled_DoesNotOverwriteCancelledState()
    {
        var history = new FakeHistoryStore();
        var alerts = new FakeAlertRuleStore();
        var deliveries = new FakeWebhookDeliveryStore();
        var service = new AnalysisJobService(history, alerts, deliveries);
        var job = await service.StartAsync(Guid.NewGuid(), "unit-test", "json");

        await service.CancelAsync(job);
        await service.FailAsync(job, new InvalidOperationException("late failure"));

        var persisted = Assert.Single(history.Jobs);
        Assert.Equal(AnalysisJobStatus.Cancelled, persisted.Status);
        Assert.Equal("分析已取消。", persisted.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteAsync_WithExistingJob_ReusesSameJobId()
    {
        var history = new FakeHistoryStore();
        var alerts = new FakeAlertRuleStore();
        var deliveries = new FakeWebhookDeliveryStore();
        var service = new AnalysisJobService(history, alerts, deliveries);
        var coordinator = new AnalysisExecutionCoordinator(service);
        var jobId = Guid.NewGuid();
        var job = await service.StartAsync(jobId, "unit-test", "json");

        var task = coordinator.ExecuteAsync(
            job,
            _ => Task.FromResult(CreateMinimalResult()),
            static result => result);

        await task;

        var persisted = Assert.Single(history.Jobs);
        Assert.Equal(jobId, persisted.Id);
        Assert.Equal(AnalysisJobStatus.Succeeded, persisted.Status);
    }

    [Fact]
    public async Task ExecuteAsync_WithExistingJob_WhenCancelled_PersistsCancelledSameJob()
    {
        var history = new FakeHistoryStore();
        var alerts = new FakeAlertRuleStore();
        var deliveries = new FakeWebhookDeliveryStore();
        var service = new AnalysisJobService(history, alerts, deliveries);
        var coordinator = new AnalysisExecutionCoordinator(service);
        var jobId = Guid.NewGuid();
        var job = await service.StartAsync(jobId, "unit-test", "json");
        using var cts = new CancellationTokenSource();

        Func<CancellationToken, Task<AnalysisResult>> execute = async token =>
        {
            cts.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return CreateMinimalResult();
        };

        var task = coordinator.ExecuteAsync(
            job,
            execute,
            static result => result,
            cancellationToken: cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);

        var persisted = Assert.Single(history.Jobs);
        Assert.Equal(jobId, persisted.Id);
        Assert.Equal(AnalysisJobStatus.Cancelled, persisted.Status);
    }

    private static AnalysisResult CreateMinimalResult()
        => new();

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

        public Task<bool> TryTransitionAsync(AnalysisJob job, AnalysisJobStatus expectedStatus, CancellationToken cancellationToken = default)
        {
            var existing = Jobs.FirstOrDefault(x => x.Id == job.Id);
            if (existing is null || existing.Status != expectedStatus)
                return Task.FromResult(false);
            var index = Jobs.IndexOf(existing);
            Jobs[index] = job;
            return Task.FromResult(true);
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
        public Task<IReadOnlyList<WebhookDelivery>> GetRecentAsync(int limit = 100, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WebhookDelivery>>([]);
        public Task<WebhookDelivery?> GetAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<WebhookDelivery?>(null);
        public Task<bool> RetryAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<WebhookDelivery?> GetNextPendingAsync(DateTime utcNow, CancellationToken cancellationToken = default) => Task.FromResult<WebhookDelivery?>(null);
        public Task MarkProcessingAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task MarkSucceededAsync(Guid id, DateTime sentAtUtc, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task MarkFailedAsync(Guid id, string errorMessage, DateTime nextAttemptAtUtc, bool permanent, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
