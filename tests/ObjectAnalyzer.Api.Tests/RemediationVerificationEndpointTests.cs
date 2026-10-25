using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using ObjectAnalyzer.Analysis;
using ObjectAnalyzer.Platform;

namespace ObjectAnalyzer.Api.Tests;

public sealed class RemediationVerificationEndpointTests
{
    [Fact]
    public async Task FailedPostProcessing_DoesNotFailAnalysisAndCanBeRetried()
    {
        await using var factory = new IsolatedApiWebApplicationFactory();
        using var client = factory.CreateClient();
        var history = factory.Services.GetRequiredService<IAnalysisHistoryStore>();
        var remediations = factory.Services.GetRequiredService<IQualityIssueRemediationStore>();
        var previous = new AnalysisJob { SourceName = "orders", SourceType = "json", Status = AnalysisJobStatus.Succeeded,
            StartedAtUtc = DateTime.UtcNow.AddDays(-1), ResultJson = "{}" };
        await history.SaveAsync(previous);
        await history.SaveQualitySnapshotAsync(new QualitySnapshot(previous.Id, previous.SourceName, DateTime.UtcNow,
            1, 1, 1, QualityMetricsCalculator.Calculate(new AnalysisResult())), [],
            [new QualityIssue(previous.Id, "value", "required", 1, "missing")]);
        await remediations.SaveAsync(new QualityIssueRemediation(Guid.NewGuid(), previous.Id, "value", "required", "fix"));
        // All stores point at the isolated test database; resolve its path from host configuration.
        var path = factory.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()["Platform:DatabasePath"]!;
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER fail_verification BEFORE UPDATE ON QualityIssueRemediations BEGIN SELECT RAISE(ABORT,'injected failure'); END;";
        await command.ExecuteNonQueryAsync();

        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<AnalysisJobService>();
        var job = await service.StartAsync("orders", "json");
        await scope.ServiceProvider.GetRequiredService<AnalysisExecutionCoordinator>()
            .ExecuteAsync(job, _ => Task.FromResult(new AnalysisResult()), x => x);
        Assert.Equal(AnalysisJobStatus.Succeeded, (await history.GetAsync(job.Id))!.Status);
        var statusUrl = $"/api/analysis/jobs/{job.Id}/remediation-verification";
        var failed = await client.GetFromJsonAsync<JsonElement>(statusUrl);
        Assert.Equal("Failed", failed.GetProperty("status").GetString());
        Assert.Equal(previous.Id, (await history.GetRemediationVerificationAsync(job.Id))!.PreviousJobId);

        command.CommandText = "DROP TRIGGER fail_verification";
        await command.ExecuteNonQueryAsync();
        using var retried = await client.PostAsync(statusUrl + "/retry", null);
        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        var success = await retried.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Completed", success.GetProperty("status").GetString());
        Assert.Equal("Completed", Assert.Single(await remediations.GetByIssueAsync(previous.Id, "value", "required")).Status);
        Assert.Equal(AnalysisJobStatus.Succeeded, (await history.GetAsync(job.Id))!.Status);
        using var retryAgain = await client.PostAsync(statusUrl + "/retry", null);
        Assert.Equal(HttpStatusCode.OK, retryAgain.StatusCode);
        Assert.True((await history.GetRemediationVerificationAsync(job.Id))!.Succeeded);
    }

    [Fact]
    public async Task Verification_ReportsMissingAndUnstartedJobs()
    {
        await using var factory = new IsolatedApiWebApplicationFactory();
        using var client = factory.CreateClient();
        var url = $"/api/analysis/jobs/{Guid.NewGuid()}/remediation-verification";
        using var missing = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        var job = new AnalysisJob { SourceName = "pending", SourceType = "json" };
        await factory.Services.GetRequiredService<IAnalysisHistoryStore>().SaveAsync(job);
        url = $"/api/analysis/jobs/{job.Id}/remediation-verification";
        var unstarted = await client.GetFromJsonAsync<JsonElement>(url);
        Assert.Equal("NotStarted", unstarted.GetProperty("status").GetString());
        using var retry = await client.PostAsync(url + "/retry", null);
        Assert.Equal(HttpStatusCode.Conflict, retry.StatusCode);
    }
}
