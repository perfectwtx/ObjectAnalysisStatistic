using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using ObjectAnalyzer.Analysis;
using ObjectAnalyzer.Platform;

namespace ObjectAnalyzer.Api.Tests;

public sealed class DeletedJobCacheTests
{
    [Fact]
    public async Task Delete_RemovesCacheAndAllAsyncEndpointsRespectMissingPersistence()
    {
        await using var factory = new IsolatedApiWebApplicationFactory();
        using var client = factory.CreateClient();
        var history = factory.Services.GetRequiredService<IAnalysisHistoryStore>();
        var job = new AnalysisJob { SourceName = "cached", SourceType = "json", Status = AnalysisJobStatus.Succeeded, ResultJson = "{}" };
        await history.SaveAsync(job);
        var id = job.Id.ToString("N");
        var registry = AnalysisJobRegistry.Instance;
        using var cts = new CancellationTokenSource();
        registry.Register(id, cts, new AnalysisJobMeta("cached", "json", null, DateTimeOffset.UtcNow));
        registry.Complete(id, new AnalysisResult());
        using var deleted = await client.DeleteAsync($"/api/analysis/jobs/{job.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Null(registry.Get(id));

        // Even a late or externally retained runtime entry cannot resurrect deleted persistence.
        using var staleCts = new CancellationTokenSource();
        registry.Register(id, staleCts, new AnalysisJobMeta("stale", "json", null, DateTimeOffset.UtcNow));
        registry.Complete(id, new AnalysisResult());
        try
        {
            using var polled = await client.GetAsync($"/api/analyze/async/{id}");
            Assert.Equal(HttpStatusCode.NotFound, polled.StatusCode);
            using var cancelled = await client.PostAsync($"/api/analyze/async/{id}/cancel", null);
            Assert.Equal(HttpStatusCode.NotFound, cancelled.StatusCode);
            using var events = await client.GetAsync($"/api/analyze/async/{id}/events");
            var text = await events.Content.ReadAsStringAsync();
            Assert.Contains("event: gone", text);
            Assert.DoesNotContain("event: complete", text);
        }
        finally { registry.Remove(id); }
    }

    [Fact]
    public async Task BulkDelete_InvalidatesEachDeletedRuntimeEntry()
    {
        await using var factory = new IsolatedApiWebApplicationFactory();
        using var client = factory.CreateClient();
        var history = factory.Services.GetRequiredService<IAnalysisHistoryStore>();
        var ids = new List<Guid>();
        var registry = AnalysisJobRegistry.Instance;
        try
        {
            for (var i = 0; i < 2; i++)
            {
                var job = new AnalysisJob { SourceName = "bulk", SourceType = "json", Status = AnalysisJobStatus.Succeeded };
                await history.SaveAsync(job);
                ids.Add(job.Id);
                registry.Register(job.Id.ToString("N"), new CancellationTokenSource(), new AnalysisJobMeta("bulk", "json", null, DateTimeOffset.UtcNow));
                registry.Complete(job.Id.ToString("N"), new AnalysisResult());
            }
            using var request = new HttpRequestMessage(HttpMethod.Delete, "/api/jobs") { Content = JsonContent.Create(new { ids }) };
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            foreach (var id in ids)
            {
                Assert.Null(registry.Get(id.ToString("N")));
                Assert.Null(await history.GetAsync(id));
            }
        }
        finally { foreach (var id in ids) registry.Remove(id.ToString("N")); }
    }
}
