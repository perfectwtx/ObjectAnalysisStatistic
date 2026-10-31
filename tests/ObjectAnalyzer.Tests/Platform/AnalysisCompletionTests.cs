using Microsoft.Data.Sqlite;
using ObjectAnalyzer.Analysis;
using ObjectAnalyzer.Platform;

namespace ObjectAnalyzer.Tests.Platform;

public sealed class AnalysisCompletionTests
{
    [Theory]
    [InlineData("quality_snapshots")]
    [InlineData("field_quality_snapshots")]
    [InlineData("quality_issues")]
    [InlineData("webhook_deliveries")]
    public async Task Complete_WhenAnyWriteFails_RollsBackEveryRecord(string table)
    {
        var path = Path.Combine(Path.GetTempPath(), $"oa-completion-{Guid.NewGuid():N}.db");
        try
        {
            var store = new SqliteAnalysisHistoryStore(path);
            await store.InitializeAsync();
            await new SqliteWebhookDeliveryStore(path).InitializeAsync();
            var job = new AnalysisJob { SourceName = "test", SourceType = "json", Status = AnalysisJobStatus.Running };
            await store.SaveAsync(job);
            await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE TRIGGER fail_write BEFORE INSERT ON {table} BEGIN SELECT RAISE(ABORT, 'injected failure'); END;";
            await command.ExecuteNonQueryAsync();

            job.Status = AnalysisJobStatus.Succeeded;
            await Assert.ThrowsAsync<SqliteException>(() => store.TryCompleteAsync(CreateCompletion(job)));

            Assert.Equal(AnalysisJobStatus.Running, (await store.GetAsync(job.Id))!.Status);
            foreach (var name in new[] { "quality_snapshots", "field_quality_snapshots", "quality_issues", "webhook_deliveries" })
            {
                command.CommandText = $"SELECT COUNT(*) FROM {name}";
                Assert.Equal(0L, await command.ExecuteScalarAsync());
            }
        }
        finally { SqliteTestDatabase.Delete(path); }
    }

    [Fact]
    public async Task Complete_CommitsOnceAndDoesNotOverwriteCancellation()
    {
        var path = Path.Combine(Path.GetTempPath(), $"oa-completion-{Guid.NewGuid():N}.db");
        try
        {
            var store = new SqliteAnalysisHistoryStore(path);
            var deliveries = new SqliteWebhookDeliveryStore(path);
            await store.InitializeAsync();
            await deliveries.InitializeAsync();
            var job = new AnalysisJob { SourceName = "test", SourceType = "json", Status = AnalysisJobStatus.Running };
            await store.SaveAsync(job);
            job.Status = AnalysisJobStatus.Succeeded;
            var completion = CreateCompletion(job);
            Assert.True(await store.TryCompleteAsync(completion));
            Assert.False(await store.TryCompleteAsync(completion));
            Assert.Single(await deliveries.GetRecentAsync());
            Assert.Equal(AnalysisJobStatus.Succeeded, (await store.GetAsync(job.Id))!.Status);

            var cancelled = new AnalysisJob { SourceName = "test", SourceType = "json", Status = AnalysisJobStatus.Cancelled };
            await store.SaveAsync(cancelled);
            cancelled.Status = AnalysisJobStatus.Succeeded;
            Assert.False(await store.TryCompleteAsync(CreateCompletion(cancelled)));
            Assert.Equal(AnalysisJobStatus.Cancelled, (await store.GetAsync(cancelled.Id))!.Status);
            Assert.Single(await deliveries.GetRecentAsync());
        }
        finally { SqliteTestDatabase.Delete(path); }
    }

    [Fact]
    public async Task Coordinator_WhenArchivingFails_PersistsFailureRatherThanSuccess()
    {
        var path = Path.Combine(Path.GetTempPath(), $"oa-completion-{Guid.NewGuid():N}.db");
        try
        {
            var history = new SqliteAnalysisHistoryStore(path);
            var alerts = new SqliteAlertRuleStore(path);
            var deliveries = new SqliteWebhookDeliveryStore(path);
            await history.InitializeAsync();
            await alerts.InitializeAsync();
            await deliveries.InitializeAsync();
            await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER fail_snapshot BEFORE INSERT ON quality_snapshots BEGIN SELECT RAISE(ABORT, 'injected failure'); END;";
            await command.ExecuteNonQueryAsync();
            var service = new AnalysisJobService(history, alerts);
            var job = await service.StartAsync("test", "json");
            await Assert.ThrowsAsync<SqliteException>(() => new AnalysisExecutionCoordinator(service)
                .ExecuteAsync(job, _ => Task.FromResult(new AnalysisResult()), x => x));
            Assert.Equal(AnalysisJobStatus.Failed, job.Status);
            Assert.Equal(AnalysisJobStatus.Failed, (await history.GetAsync(job.Id))!.Status);
            Assert.Null((await history.GetAsync(job.Id))!.ResultJson);
        }
        finally { SqliteTestDatabase.Delete(path); }
    }

    private static AnalysisCompletion CreateCompletion(AnalysisJob job) => new(job,
        new QualitySnapshot(job.Id, job.SourceName, DateTime.UtcNow, 1, 1, 1,
            QualityMetricsCalculator.Calculate(new AnalysisResult())),
        [new FieldQualitySnapshot(job.Id, "value", 1, 0, 1, 0)],
        [new QualityIssue(job.Id, "value", "required", 1, "missing")],
        [new WebhookDelivery { JobId = job.Id, WebhookUrl = "https://example.invalid", PayloadJson = "{}" }]);
}
