using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using ObjectAnalyzer.Analysis;
using ObjectAnalyzer.Platform;

namespace ObjectAnalyzer.Web.Tests;

public sealed class HistoricalIssuePageTests
{
    [Fact]
    public async Task IssuePages_ResolveRequestedJobsOutsideRecentList()
    {
        var path = Path.Combine(Path.GetTempPath(), $"oa-web-old-issues-{Guid.NewGuid():N}.db");
        await using var factory = new TestWebApplicationFactory().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddAnalysisPlatform(path)));
        using var client = factory.CreateClient();
        var history = factory.Services.GetRequiredService<IAnalysisHistoryStore>();
        var old = new AnalysisJob
        {
            SourceName = "old-web-source", SourceType = "json", Status = AnalysisJobStatus.Succeeded,
            StartedAtUtc = DateTime.UtcNow.AddDays(-5)
        };
        await history.SaveAsync(old);
        await history.SaveQualitySnapshotAsync(new QualitySnapshot(old.Id, old.SourceName,
            DateTime.UtcNow, 1, 1, 1, QualityMetricsCalculator.Calculate(new AnalysisResult())), [],
            [new QualityIssue(old.Id, "historical-field-marker", "required", 1, "missing")]);
        for (var i = 0; i < 101; i++)
            await history.SaveAsync(new AnalysisJob { SourceName = $"newer-{i}", SourceType = "json", Status = AnalysisJobStatus.Succeeded });

        foreach (var route in new[] { "/issues", "/issues/responsibility", "/issues/detail" })
        {
            using var response = await client.GetAsync($"{route}?jobId={old.Id}&field=historical-field-marker&check=required");
            response.EnsureSuccessStatusCode();
            Assert.Contains("historical-field-marker", await response.Content.ReadAsStringAsync());
        }
    }
}
