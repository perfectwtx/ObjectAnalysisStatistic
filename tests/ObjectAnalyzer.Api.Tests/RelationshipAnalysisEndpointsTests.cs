using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ObjectAnalyzer.Analysis;
using ObjectAnalyzer.Platform;

namespace ObjectAnalyzer.Api.Tests;

/// <summary>关系分析相关 API 的边界集成测试。</summary>
public class RelationshipAnalysisEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public RelationshipAnalysisEndpointsTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Lineage_MissingJob_ReturnsNotFound()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync($"/api/analysis/jobs/{Guid.NewGuid()}/lineage");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Impact_MissingJob_ReturnsNotFound()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync($"/api/analysis/jobs/{Guid.NewGuid()}/impact");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Lineage_InvalidJobId_ReturnsNotFound()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/analysis/jobs/not-a-guid/lineage");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Impact_InvalidJobId_ReturnsNotFound()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/analysis/jobs/not-a-guid/impact");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}

/// <summary>关系分析成功响应的端到端集成测试，使用内存历史存储隔离真实数据库。</summary>
public sealed class RelationshipAnalysisSuccessfulEndpointsTests
    : IClassFixture<RelationshipAnalysisWebApplicationFactory>
{
    private readonly RelationshipAnalysisWebApplicationFactory _factory;

    public RelationshipAnalysisSuccessfulEndpointsTests(RelationshipAnalysisWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Lineage_SucceededJob_ReturnsNodesAndEdges()
    {
        var job = CreateSucceededJob(new AnalysisResult
        {
            ObjectRelationships =
            [
                new ObjectRelationship
                {
                    Path = "customer",
                    Kind = "Object",
                    OccurrenceCount = 3,
                    NonNullCount = 3,
                    TargetFieldCount = 2,
                    TargetFields = ["id", "name"]
                }
            ]
        });
        _factory.Store.Set(job);

        using var client = _factory.CreateClient();
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
            ObjectRelationships =
            [
                new ObjectRelationship
                {
                    Path = "customer",
                    Kind = "Object",
                    OccurrenceCount = 1,
                    NonNullCount = 1,
                    TargetFieldCount = 1,
                    TargetFields = ["id"]
                }
            ]
        });
        _factory.Store.Set(job);

        using var client = _factory.CreateClient();
        var response = await client.GetAsync($"/api/analysis/jobs/{job.Id}/impact");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("nodes").GetArrayLength());
        Assert.Equal("Low", body.GetProperty("highestLevel").GetString());
        Assert.Equal(0, body.GetProperty("breakingChangeCount").GetInt32());
    }

    [Fact]
    public async Task Lineage_FailedJob_ReturnsConflict()
    {
        var job = new AnalysisJob
        {
            Id = Guid.NewGuid(),
            SourceName = "failed.json",
            SourceType = "json",
            Status = AnalysisJobStatus.Failed
        };
        _factory.Store.Set(job);

        using var client = _factory.CreateClient();
        var response = await client.GetAsync($"/api/analysis/jobs/{job.Id}/lineage");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Impact_RunningJob_ReturnsConflict()
    {
        var job = new AnalysisJob
        {
            Id = Guid.NewGuid(),
            SourceName = "running.json",
            SourceType = "json",
            Status = AnalysisJobStatus.Running
        };
        _factory.Store.Set(job);

        using var client = _factory.CreateClient();
        var response = await client.GetAsync($"/api/analysis/jobs/{job.Id}/impact");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    private static AnalysisJob CreateSucceededJob(AnalysisResult result)
        => new()
        {
            Id = Guid.NewGuid(),
            SourceName = "orders.json",
            SourceType = "json",
            Status = AnalysisJobStatus.Succeeded,
            CompletedAtUtc = DateTime.UtcNow,
            ResultJson = JsonSerializer.Serialize(result)
        };
}

/// <summary>为关系分析成功响应测试提供隔离的内存历史存储。</summary>
public sealed class RelationshipAnalysisWebApplicationFactory : WebApplicationFactory<Program>
{
    public InMemoryRelationshipHistoryStore Store { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IAnalysisHistoryStore>();
            services.AddSingleton<IAnalysisHistoryStore>(Store);
        });
    }
}

internal sealed class InMemoryRelationshipHistoryStore : IAnalysisHistoryStore
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
