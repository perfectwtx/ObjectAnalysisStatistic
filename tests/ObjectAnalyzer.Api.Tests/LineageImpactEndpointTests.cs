using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ObjectAnalyzer.Analysis;
using ObjectAnalyzer.Platform;

namespace ObjectAnalyzer.Api.Tests;

/// <summary>数据血缘与影响分析接口的最小集成回归测试。</summary>
public sealed class LineageImpactEndpointTests : IClassFixture<LineageImpactWebApplicationFactory>
{
    private readonly LineageImpactWebApplicationFactory _factory;

    public LineageImpactEndpointTests(LineageImpactWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Lineage_MissingJob_Returns404()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync($"/api/analysis/jobs/{Guid.NewGuid()}/lineage");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Impact_MissingJob_Returns404()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync($"/api/analysis/jobs/{Guid.NewGuid()}/impact");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Lineage_NonSuccessfulJob_Returns409()
    {
        var job = new AnalysisJob
        {
            Id = Guid.NewGuid(),
            SourceName = "failed.json",
            SourceType = "json",
            Status = AnalysisJobStatus.Failed,
            ResultJson = null
        };
        _factory.Store.Set(job);

        var client = _factory.CreateClient();
        var response = await client.GetAsync($"/api/analysis/jobs/{job.Id}/lineage");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Impact_NonSuccessfulJob_Returns409()
    {
        var job = new AnalysisJob
        {
            Id = Guid.NewGuid(),
            SourceName = "running.json",
            SourceType = "json",
            Status = AnalysisJobStatus.Running,
            ResultJson = null
        };
        _factory.Store.Set(job);

        var client = _factory.CreateClient();
        var response = await client.GetAsync($"/api/analysis/jobs/{job.Id}/impact");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Lineage_SucceededJob_ReturnsNodesAndEdges()
    {
        var job = CreateSucceededJob(new AnalysisResult
        {
            ObjectRelationships = new List<ObjectRelationship>
            {
                new()
                {
                    Path = "customer",
                    Kind = "Object",
                    OccurrenceCount = 3,
                    NonNullCount = 3,
                    TargetFieldCount = 2,
                    TargetFields = ["id", "name"]
                }
            }
        });
        _factory.Store.Set(job);

        var client = _factory.CreateClient();
        var response = await client.GetAsync($"/api/analysis/jobs/{job.Id}/lineage");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, body.GetProperty("nodes").GetArrayLength());
        Assert.Equal(2, body.GetProperty("edges").GetArrayLength());
        Assert.Contains(
            body.GetProperty("nodes").EnumerateArray(),
            node => node.GetProperty("path").GetString() == "customer.name");
    }

    [Fact]
    public async Task Impact_SucceededJobWithoutSchemaDiff_ReturnsEmptyLowImpact()
    {
        var job = CreateSucceededJob(new AnalysisResult
        {
            ObjectRelationships = new List<ObjectRelationship>
            {
                new()
                {
                    Path = "customer",
                    Kind = "Object",
                    OccurrenceCount = 1,
                    NonNullCount = 1,
                    TargetFieldCount = 1,
                    TargetFields = ["id"]
                }
            }
        });
        _factory.Store.Set(job);

        var client = _factory.CreateClient();
        var response = await client.GetAsync($"/api/analysis/jobs/{job.Id}/impact");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("nodes").GetArrayLength());
        Assert.Equal("Low", body.GetProperty("highestLevel").GetString());
        Assert.Equal(0, body.GetProperty("breakingChangeCount").GetInt32());
    }

    private static AnalysisJob CreateSucceededJob(AnalysisResult result)
    {
        return new AnalysisJob
        {
            Id = Guid.NewGuid(),
            SourceName = "orders.json",
            SourceType = "json",
            Status = AnalysisJobStatus.Succeeded,
            CompletedAtUtc = DateTime.UtcNow,
            ResultJson = JsonSerializer.Serialize(result)
        };
    }
}

/// <summary>为 API 集成测试提供可控的内存历史存储，避免污染真实 SQLite 数据库。</summary>
public sealed class LineageImpactWebApplicationFactory : WebApplicationFactory<Program>
{
    public InMemoryAnalysisHistoryStore Store { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IAnalysisHistoryStore>();
            services.AddSingleton<IAnalysisHistoryStore>(Store);
        });
    }
}

internal sealed class InMemoryAnalysisHistoryStore : IAnalysisHistoryStore
{
    private readonly Dictionary<Guid, AnalysisJob> _jobs = new();

    public void Set(AnalysisJob job) => _jobs[job.Id] = job;

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task SaveAsync(AnalysisJob job, CancellationToken cancellationToken = default)
    {
        _jobs[job.Id] = job;
        return Task.CompletedTask;
    }

    public Task SaveQualitySnapshotAsync(
        QualitySnapshot snapshot,
        IReadOnlyList<FieldQualitySnapshot> fields,
        IReadOnlyList<QualityIssue> issues,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<AnalysisJob?> GetAsync(Guid id, CancellationToken cancellationToken = default)
        => Task.FromResult(_jobs.GetValueOrDefault(id));

    public Task<IReadOnlyList<AnalysisJob>> GetRecentAsync(int limit = 50, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<AnalysisJob>>(_jobs.Values.Take(limit).ToList());
}
