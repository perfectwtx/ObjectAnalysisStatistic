using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ObjectAnalyzer.Analysis;
using ObjectAnalyzer.Platform;

namespace ObjectAnalyzer.Api.Tests;

public sealed class AsyncJobIdFormatTests
{
    [Theory]
    [InlineData("D")]
    [InlineData("N")]
    [InlineData("B")]
    public async Task Aliases_CanReadProgressAndCancelTheSameJob(string format)
    {
        await using var factory = new IsolatedApiWebApplicationFactory();
        using var client = factory.CreateClient();
        var history = factory.Services.GetRequiredService<IAnalysisHistoryStore>();
        var job = new AnalysisJob { SourceName = "format", SourceType = "json", Status = AnalysisJobStatus.Running };
        await history.SaveAsync(job);
        var canonical = job.Id.ToString("N");
        var alias = Uri.EscapeDataString(job.Id.ToString(format).ToUpperInvariant());
        var registry = AnalysisJobRegistry.Instance;
        using var cts = new CancellationTokenSource();
        registry.Register(canonical, cts, new AnalysisJobMeta("format", "json", null, DateTimeOffset.UtcNow));
        try
        {
            registry.ReportProgress(canonical, 42);
            var running = await client.GetFromJsonAsync<JsonElement>($"/api/analyze/async/{alias}");
            Assert.Equal(42, running.GetProperty("processedObjects").GetInt64());
            using var response = await client.PostAsync($"/api/analyze/async/{alias}/cancel", null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var cancelled = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(cancelled.GetProperty("cancelled").GetBoolean());
            Assert.True(cts.IsCancellationRequested);
            using var events = await client.GetAsync($"/api/analyze/async/{alias}/events");
            var text = await events.Content.ReadAsStringAsync();
            Assert.Contains("event: complete", text);
            Assert.Contains("Cancelled", text);
        }
        finally { registry.Remove(canonical); }
    }
}
