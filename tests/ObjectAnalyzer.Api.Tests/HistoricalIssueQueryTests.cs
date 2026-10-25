using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ObjectAnalyzer.Analysis;
using ObjectAnalyzer.Platform;

namespace ObjectAnalyzer.Api.Tests;

public sealed class HistoricalIssueQueryTests
{
    [Fact]
    public async Task OldJob_RemainsQueryableWithSqlFiltersPaginationAndSummary()
    {
        await using var factory = new IsolatedApiWebApplicationFactory();
        using var client = factory.CreateClient();
        var history = factory.Services.GetRequiredService<IAnalysisHistoryStore>();
        var responsibilities = factory.Services.GetRequiredService<IQualityIssueResponsibilityStore>();
        var old = new AnalysisJob
        {
            SourceName = "old-source", SourceType = "json", Status = AnalysisJobStatus.Succeeded,
            StartedAtUtc = DateTime.UtcNow.AddDays(-5),
            ResultJson = JsonSerializer.Serialize(new AnalysisResult { TotalObjects = 2 })
        };
        await history.SaveAsync(old);
        await history.SaveQualitySnapshotAsync(new QualitySnapshot(old.Id, old.SourceName,
            DateTime.UtcNow, 2, 3, 3, QualityMetricsCalculator.Calculate(new AnalysisResult())), [],
            [new QualityIssue(old.Id, "alpha", "required", 8, "required"),
             new QualityIssue(old.Id, "beta", "unique", 9, "unique"),
             new QualityIssue(old.Id, "gamma", "range", 3, "range")]);
        await responsibilities.SaveAsync(new QualityIssueResponsibility(old.Id, "ALPHA", "REQUIRED", "owner", "InProgress", "assigned", DateTime.UtcNow));
        for (var i = 0; i < 101; i++)
            await history.SaveAsync(new AnalysisJob { SourceName = $"newer-{i}", SourceType = "json", Status = AnalysisJobStatus.Succeeded });

        var page = await client.GetFromJsonAsync<JsonElement>($"/api/issues?jobId={old.Id}&limit=1&offset=1");
        Assert.Equal(3, page.GetProperty("total").GetInt32());
        Assert.Equal(old.Id, page.GetProperty("selectedJob").GetProperty("id").GetGuid());
        Assert.Equal("alpha", Assert.Single(page.GetProperty("items").EnumerateArray()).GetProperty("field").GetString());
        Assert.All(page.GetProperty("jobs").EnumerateArray(), x => Assert.False(x.TryGetProperty("resultJson", out _)));

        var filtered = await client.GetFromJsonAsync<JsonElement>($"/api/issues?jobId={old.Id}&check=REQUIRED&status=inprogress&severity=HIGH");
        Assert.Equal(1, filtered.GetProperty("total").GetInt32());
        Assert.Equal("owner", Assert.Single(filtered.GetProperty("items").EnumerateArray()).GetProperty("owner").GetString());
        var beyond = await client.GetFromJsonAsync<JsonElement>($"/api/issues?jobId={old.Id}&offset=99");
        Assert.Equal(3, beyond.GetProperty("total").GetInt32());
        Assert.Empty(beyond.GetProperty("items").EnumerateArray());

        var summary = await client.GetFromJsonAsync<JsonElement>($"/api/issues/summary?jobId={old.Id}");
        Assert.Equal(3, summary.GetProperty("totalIssues").GetInt32());
        Assert.Equal(20, summary.GetProperty("totalViolations").GetInt64());
        Assert.Equal(2, summary.GetProperty("byStatus").GetProperty("Open").GetInt32());
        Assert.Equal(1, summary.GetProperty("byStatus").GetProperty("InProgress").GetInt32());
        var all = await client.GetFromJsonAsync<JsonElement>("/api/issues/summary");
        Assert.Equal(3, all.GetProperty("totalIssues").GetInt32());

        var jobs = await client.GetFromJsonAsync<JsonElement>("/api/analysis/jobs?limit=1&offset=101");
        var listed = Assert.Single(jobs.EnumerateArray());
        Assert.Equal(old.Id, listed.GetProperty("id").GetGuid());
        Assert.False(listed.TryGetProperty("resultJson", out _));
        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/analysis/jobs/{old.Id}/result");
        Assert.Equal(2, detail.GetProperty("totalObjects").GetInt64());
    }
}
