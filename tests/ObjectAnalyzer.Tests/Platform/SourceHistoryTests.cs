using System.Text.Json;
using Microsoft.Data.Sqlite;
using ObjectAnalyzer.Analysis;
using ObjectAnalyzer.Platform;

namespace ObjectAnalyzer.Tests.Platform;

public sealed class SourceHistoryTests
{
    [Fact]
    public async Task Completion_FindsOldSourceForRegressionAndRemediation()
    {
        var path = Path.Combine(Path.GetTempPath(), $"oa-source-history-{Guid.NewGuid():N}.db");
        try
        {
            var history = new SqliteAnalysisHistoryStore(path);
            var alerts = new SqliteAlertRuleStore(path);
            var outbox = new SqliteWebhookDeliveryStore(path);
            var responsibilities = new SqliteQualityIssueResponsibilityStore(path);
            var remediations = new SqliteQualityIssueRemediationStore(path);
            await history.InitializeAsync(); await alerts.InitializeAsync(); await outbox.InitializeAsync();
            await responsibilities.InitializeAsync(); await remediations.InitializeAsync();
            var previous = new AnalysisJob { SourceName = "Orders", SourceType = "json", Status = AnalysisJobStatus.Succeeded,
                QualityScore = 100, StartedAtUtc = DateTime.UtcNow.AddDays(-3), ResultJson = "{}" };
            await history.SaveAsync(previous);
            await history.SaveQualitySnapshotAsync(new QualitySnapshot(previous.Id, previous.SourceName, DateTime.UtcNow,
                1, 1, 1, QualityMetricsCalculator.Calculate(new AnalysisResult())), [],
                [new QualityIssue(previous.Id, "value", "required", 1, "missing")]);
            await remediations.SaveAsync(new QualityIssueRemediation(Guid.NewGuid(), previous.Id, "value", "required", "fix"));
            for (var i = 0; i < 101; i++)
                await history.SaveAsync(new AnalysisJob { SourceName = "other", SourceType = "json", Status = AnalysisJobStatus.Succeeded });
            await history.SaveAsync(new AnalysisJob { SourceName = "orders", SourceType = "json", Status = AnalysisJobStatus.Failed });
            await alerts.SaveAsync(new AlertRule { Name = "regression", QualityRegressionBelow = 1, WebhookUrl = "https://example.invalid" });
            var verifier = new QualityIssueRemediationVerifier(new SqliteQualityIssueQuery(path), responsibilities, remediations);
            var service = new AnalysisJobService(history, alerts, remediationVerifier: verifier);
            var job = await service.StartAsync("orders", "json");
            var result = Analysis.ObjectAnalyzer.Analyze(new[] { new Dictionary<string, object?> { ["value"] = 1 }, new Dictionary<string, object?>() });
            Assert.True(await service.CompleteAsync(job, result));
            var delivery = Assert.Single(await outbox.GetRecentAsync());
            using var payload = JsonDocument.Parse(delivery.PayloadJson);
            Assert.Equal(100, payload.RootElement.GetProperty("PreviousScore").GetDouble());
            Assert.Equal(85, payload.RootElement.GetProperty("CurrentScore").GetDouble());
            Assert.Equal("Completed", Assert.Single(await remediations.GetByIssueAsync(previous.Id, "value", "required")).Status);
        }
        finally
        {
            using var pool = new SqliteConnection($"Data Source={path}");
            SqliteConnection.ClearPool(pool);
            SqliteTestDatabase.Delete(path);
        }
    }
}
