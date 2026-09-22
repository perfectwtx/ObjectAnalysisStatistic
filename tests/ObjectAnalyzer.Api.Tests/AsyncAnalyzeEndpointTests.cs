using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ObjectAnalyzer.Analysis;
using ObjectAnalyzer.Platform;

namespace ObjectAnalyzer.Api.Tests;

/// <summary>异步分析作业查询在运行时 Registry 清理后仍可从持久化历史恢复。</summary>
public sealed class AsyncAnalyzeEndpointTests : IClassFixture<AsyncAnalyzeWebApplicationFactory>
{
    private readonly AsyncAnalyzeWebApplicationFactory _factory;

    public AsyncAnalyzeEndpointTests(AsyncAnalyzeWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task GetAsyncJob_WhenRegistryHasNoSnapshot_FallsBackToPersistedCompletedJob()
    {
        var job = new AnalysisJob
        {
            Id = Guid.NewGuid(),
            SourceName = "orders.json",
            SourceType = "json",
            Status = AnalysisJobStatus.Succeeded,
            StartedAtUtc = DateTime.UtcNow.AddMinutes(-2),
            CompletedAtUtc = DateTime.UtcNow.AddMinutes(-1),
            TotalObjects = 3,
            ResultJson = JsonSerializer.Serialize(new AnalysisResult { TotalObjects = 3 })
        };
        _factory.Store.Set(job);

        using var client = _factory.CreateClient();
        var response = await client.GetAsync($"/api/analyze/async/{job.Id:N}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(job.Id.ToString("N"), body.GetProperty("id").GetString());
        Assert.Equal("Completed", body.GetProperty("status").GetString());
        Assert.Equal("orders.json", body.GetProperty("meta").GetProperty("dataFileName").GetString());
        Assert.Equal("json", body.GetProperty("meta").GetProperty("format").GetString());
        Assert.Equal(3, body.GetProperty("processedObjects").GetInt64());
        Assert.Equal(3, body.GetProperty("result").GetProperty("totalObjects").GetInt64());
    }

    [Fact]
    public async Task GetAsyncJob_WhenRegistryHasNoSnapshot_ReturnsPersistedFailedJob()
    {
        var job = new AnalysisJob
        {
            Id = Guid.NewGuid(),
            SourceName = "broken.json",
            SourceType = "json",
            Status = AnalysisJobStatus.Failed,
            StartedAtUtc = DateTime.UtcNow.AddMinutes(-1),
            CompletedAtUtc = DateTime.UtcNow,
            ErrorMessage = "解析失败"
        };
        _factory.Store.Set(job);

        using var client = _factory.CreateClient();
        var response = await client.GetAsync($"/api/analyze/async/{job.Id:N}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Failed", body.GetProperty("status").GetString());
        Assert.Equal("解析失败", body.GetProperty("error").GetString());
        Assert.False(body.TryGetProperty("result", out var result) && result.ValueKind != JsonValueKind.Null);
    }

    [Fact]
    public async Task GetAsyncJob_WhenPersistedJobDoesNotExist_ReturnsNotFound()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync($"/api/analyze/async/{Guid.NewGuid():N}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}

public sealed class AsyncAnalyzeWebApplicationFactory : WebApplicationFactory<Program>
{
    public InMemoryAsyncHistoryStore Store { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IAnalysisHistoryStore>();
            services.AddSingleton<IAnalysisHistoryStore>(Store);
        });
    }
}

public sealed class InMemoryAsyncHistoryStore : IAnalysisHistoryStore
{
    private readonly Dictionary<Guid, AnalysisJob> _jobs = new();

    public void Set(AnalysisJob job) => _jobs[job.Id] = job;

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task SaveAsync(AnalysisJob job, CancellationToken cancellationToken = default)
    {
        _jobs[job.Id] = job;
        return Task.CompletedTask;
    }

    public Task SaveQualitySnapshotAsync(QualitySnapshot snapshot, IReadOnlyList<FieldQualitySnapshot> fields, IReadOnlyList<QualityIssue> issues, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<AnalysisJob?> GetAsync(Guid id, CancellationToken cancellationToken = default)
        => Task.FromResult(_jobs.GetValueOrDefault(id));

    public Task<IReadOnlyList<AnalysisJob>> GetRecentAsync(int limit = 50, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<AnalysisJob>>(_jobs.Values.Take(limit).ToList());

    public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        => Task.FromResult(_jobs.Remove(id));
}
