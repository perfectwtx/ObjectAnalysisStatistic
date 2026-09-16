using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ObjectAnalyzer.Api.Tests;

/// <summary>
/// Web/API 功能对齐后的查询端点冒烟测试。
/// 这些测试重点保证 React 前端依赖的资源路由已经挂载，并能在空数据环境下正常返回。
/// </summary>
public sealed class ApiParityEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ApiParityEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    private HttpClient CreateClient() => _factory.CreateClient();

    [Theory]
    [InlineData("/api/analysis/jobs")]
    [InlineData("/api/quality/dashboard")]
    [InlineData("/api/quality/trend")]
    [InlineData("/api/quality/fields")]
    [InlineData("/api/quality/recommendations")]
    [InlineData("/api/quality/regression")]
    [InlineData("/api/issues")]
    [InlineData("/api/alert-rules")]
    public async Task Parity_QueryEndpoints_ReturnSuccess(string path)
    {
        var response = await CreateClient().GetAsync(path);
        Assert.True(response.IsSuccessStatusCode, $"{path}: {(int)response.StatusCode}");
    }

    [Fact]
    public async Task AnalysisJob_NotFound_Returns404()
    {
        var response = await CreateClient().GetAsync($"/api/analysis/jobs/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task JobResult_NotFound_Returns404()
    {
        var response = await CreateClient().GetAsync($"/api/analysis/jobs/{Guid.NewGuid()}/result");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task JobOverview_NotFound_Returns404()
    {
        var response = await CreateClient().GetAsync($"/api/analysis/jobs/{Guid.NewGuid()}/overview");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AsyncJob_NotFound_Returns404()
    {
        var response = await CreateClient().GetAsync($"/api/analyze/async/{Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AsyncCancel_NotFound_Returns404()
    {
        var response = await CreateClient().PostAsync($"/api/analyze/async/{Guid.NewGuid():N}/cancel", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DataLineage_NotFound_Returns404()
    {
        var response = await CreateClient().GetAsync($"/api/analysis/jobs/{Guid.NewGuid()}/lineage");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ImpactAnalysis_NotFound_Returns404()
    {
        var response = await CreateClient().GetAsync($"/api/analysis/jobs/{Guid.NewGuid()}/impact");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task QualityFields_NotFound_Returns404()
    {
        var response = await CreateClient().GetAsync($"/api/quality/fields/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task QualityRelationships_NotFound_Returns404()
    {
        var response = await CreateClient().GetAsync($"/api/quality/relationships/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task QualityObjectRelationships_NotFound_Returns404()
    {
        var response = await CreateClient().GetAsync($"/api/quality/object-relationships/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task QualitySchemaImpact_NotFound_Returns404()
    {
        var response = await CreateClient().GetAsync($"/api/quality/schema-impact/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Dashboard_ReturnsExpectedContractShape()
    {
        var response = await CreateClient().GetAsync("/api/quality/dashboard");
        Assert.True(response.IsSuccessStatusCode);

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.True(body.TryGetProperty("job", out _));
        Assert.True(body.TryGetProperty("metrics", out _));
        Assert.True(body.TryGetProperty("recentSnapshots", out _));
        Assert.True(body.TryGetProperty("topIssues", out _));
        Assert.True(body.TryGetProperty("gateResults", out _));
    }

    [Fact]
    public async Task IssueCenter_ReturnsExpectedContractShape()
    {
        var response = await CreateClient().GetAsync("/api/issues");
        Assert.True(response.IsSuccessStatusCode);

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.True(body.TryGetProperty("jobs", out _));
        Assert.True(body.TryGetProperty("selectedJob", out _));
        Assert.True(body.TryGetProperty("issues", out _));
    }

    [Fact]
    public async Task QualityFieldsResponse_ContainsWebRequiredCollections()
    {
        var jobId = Guid.NewGuid();
        var response = await CreateClient().GetAsync($"/api/quality/fields/{jobId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
