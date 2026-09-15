using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

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
    public async Task Lineage_InvalidJobId_ReturnsBadRequest()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/analysis/jobs/not-a-guid/lineage");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Impact_InvalidJobId_ReturnsBadRequest()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/analysis/jobs/not-a-guid/impact");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
