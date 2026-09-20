using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using ObjectAnalyzer.Api;

namespace ObjectAnalyzer.Api.Tests;

/// <summary>根据前端 gaps 文档覆盖新增 API、路径别名与运营接口的契约测试。</summary>
public sealed class FrontendApiGapEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public FrontendApiGapEndpointTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private HttpClient CreateClient() => _factory.CreateClient();

    [Fact]
    public async Task Capabilities_BothPaths_ReturnCompleteFlags()
    {
        foreach (var path in new[] { "/api/capabilities", "/api/meta/capabilities" })
        {
            var response = await CreateClient().GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            foreach (var property in new[]
                     {
                         "analyze", "asyncAnalyze", "preflight", "qualityDashboard",
                         "qualityTrend", "issues", "baselines", "alertRules",
                         "lineage", "impact", "webhooks"
                     })
            {
                Assert.True(body.TryGetProperty(property, out var value), $"{path}: missing {property}");
                Assert.True(value.GetBoolean(), $"{path}: {property} should be enabled");
            }
        }
    }

    [Theory]
    [InlineData("/api/jobs")]
    [InlineData("/api/analyze/jobs")]
    [InlineData("/api/analysis/jobs")]
    [InlineData("/api/quality/snapshots")]
    [InlineData("/api/quality-snapshots")]
    [InlineData("/api/analysis/quality/snapshots")]
    [InlineData("/api/quality/trend")]
    [InlineData("/api/issues/summary")]
    [InlineData("/api/alert-rules")]
    [InlineData("/api/alerts/rules")]
    [InlineData("/api/quality/alert-rules")]
    [InlineData("/api/webhooks/deliveries")]
    [InlineData("/api/health")]
    [InlineData("/api/ready")]
    public async Task FrontendCompatibilityPaths_ReturnSuccess(string path)
    {
        var response = await CreateClient().GetAsync(path);
        Assert.True(response.IsSuccessStatusCode, $"{path}: {(int)response.StatusCode}");
    }

    [Fact]
    public async Task Jobs_FieldAndSchemaAliases_NotFoundForUnknownJob()
    {
        var client = CreateClient();
        var id = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/jobs/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/jobs/{id}/result")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/jobs/{id}/fields")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/jobs/{id}/schema-impact")).StatusCode);
    }

    [Fact]
    public async Task RelationshipAliases_NotFoundForUnknownJob()
    {
        var client = CreateClient();
        var id = Guid.NewGuid();

        foreach (var path in new[]
                 {
                     $"/api/jobs/{id}/lineage",
                     $"/api/analyze/async/{id}/lineage",
                     $"/api/jobs/{id}/impact",
                     $"/api/analyze/async/{id}/impact"
                 })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);
        }
    }

    [Fact]
    public async Task QualitySnapshots_FieldAlias_NotFoundForUnknownJob()
    {
        var response = await CreateClient().GetAsync($"/api/quality/snapshots/{Guid.NewGuid()}/fields");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task BaselineFrontendContract_CrudAndEvaluation()
    {
        var client = CreateClient();
        var sourceName = $"frontend-gap-{Guid.NewGuid():N}";

        try
        {
            var create = await client.PostAsJsonAsync("/api/quality-baselines", new
            {
                name = "生产主链路基线",
                sourceName,
                minOverallScore = 80,
                minCompleteness = 0.95
            });
            Assert.Equal(HttpStatusCode.OK, create.StatusCode);
            var created = await create.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(sourceName, created.GetProperty("sourceName").GetString());
            Assert.Equal(80, created.GetProperty("minOverall").GetDouble());
            Assert.Equal(95, created.GetProperty("minCompleteness").GetDouble());

            var getAlias = await client.GetAsync($"/api/quality/baselines/{Uri.EscapeDataString(sourceName)}");
            Assert.Equal(HttpStatusCode.OK, getAlias.StatusCode);

            var update = await client.PutAsJsonAsync($"/api/quality/baselines/{Uri.EscapeDataString(sourceName)}", new
            {
                sourceName,
                minOverallScore = 82
            });
            Assert.Equal(HttpStatusCode.OK, update.StatusCode);

            var evaluate = await client.PostAsync(
                $"/api/quality-baselines/{Uri.EscapeDataString(sourceName)}/evaluate?jobId={Guid.NewGuid()}",
                content: null);
            Assert.Equal(HttpStatusCode.NotFound, evaluate.StatusCode);
        }
        finally
        {
            await client.DeleteAsync($"/api/quality-baselines/{Uri.EscapeDataString(sourceName)}");
        }
    }

    [Fact]
    public async Task AlertFrontendContract_AcceptsConditionThreshold()
    {
        var client = CreateClient();

        var create = await client.PostAsJsonAsync("/api/alerts/rules", new
        {
            name = "质量分低于 70",
            enabled = true,
            condition = "overallScoreBelow",
            threshold = 70,
            webhookUrl = "https://example.com/hooks/oa"
        });

        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetGuid();

        try
        {
            Assert.Equal("overallScoreBelow", created.GetProperty("condition").GetString());
            Assert.Equal(70, created.GetProperty("threshold").GetDouble());

            var get = await client.GetAsync($"/api/quality/alert-rules/{id}");
            Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        }
        finally
        {
            await client.DeleteAsync($"/api/alert-rules/{id}");
        }
    }

    [Fact]
    public async Task IssueSummary_ReturnsStructuredAggregation()
    {
        var response = await CreateClient().GetAsync("/api/issues/summary");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("totalIssues", out _));
        Assert.True(body.TryGetProperty("totalViolations", out _));
        Assert.Equal(JsonValueKind.Object, body.GetProperty("bySeverity").ValueKind);
        Assert.Equal(JsonValueKind.Object, body.GetProperty("byCheck").ValueKind);
        Assert.Equal(JsonValueKind.Object, body.GetProperty("byField").ValueKind);
        Assert.Equal(JsonValueKind.Object, body.GetProperty("byStatus").ValueKind);
    }

    [Fact]
    public async Task IssueStatus_InvalidJob_ReturnsNotFound()
    {
        var client = CreateClient();
        var id = Guid.NewGuid();
        var url = $"/api/issues/{id}/{Uri.EscapeDataString("field")}/{Uri.EscapeDataString("check")}/status";

        var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Patch, url)
        {
            Content = JsonContent.Create(new { status = "Resolved" })
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task IssueStatus_InvalidValue_ReturnsBadRequest()
    {
        var client = CreateClient();
        var response = await client.SendAsync(new HttpRequestMessage(
            HttpMethod.Patch,
            $"/api/issues/{Guid.NewGuid()}/field/check/status")
        {
            Content = JsonContent.Create(new { status = "Unknown" })
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task WebhookRetry_MissingDelivery_ReturnsNotFound()
    {
        var response = await CreateClient().PostAsync($"/api/webhooks/deliveries/{Guid.NewGuid()}/retry", content: null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Export_MissingJob_ReturnsNotFound()
    {
        var response = await CreateClient().GetAsync($"/api/jobs/{Guid.NewGuid()}/export?format=json");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
