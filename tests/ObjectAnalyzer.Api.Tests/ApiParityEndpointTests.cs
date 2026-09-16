using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ObjectAnalyzer.Api.Tests;

/// <summary>
/// Web/API 功能对齐后的查询端点契约测试。
/// 除了空数据路由冒烟测试，还通过真实异步分析作业验证详情类接口的响应形状。
/// </summary>
public sealed class ApiParityEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ApiParityEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    private HttpClient CreateClient() => _factory.CreateClient();

    private const string DataJson = """[{"name":"alice","age":30},{"name":"bob","age":25},{"name":"carol","age":41}]""";

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
    public async Task JobOverview_ReturnsWebRequiredSections()
    {
        var jobId = await RunAsyncAnalysisAsync();
        var response = await CreateClient().GetAsync($"/api/analysis/jobs/{jobId}/overview");

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(body.TryGetProperty("job", out _));
        Assert.True(body.TryGetProperty("metrics", out _));
        Assert.True(body.TryGetProperty("gateResults", out _));
        Assert.True(body.TryGetProperty("issues", out _));
        Assert.True(body.TryGetProperty("objectRelationships", out var objectRelationships));
        Assert.Equal(JsonValueKind.Array, objectRelationships.ValueKind);

        // 这些字段在当前简单数据集上可能为 null，因此受全局 null 忽略策略影响而不存在。
        Assert.True(!body.TryGetProperty("baselineEvaluation", out var baseline) || baseline.ValueKind == JsonValueKind.Object);
        Assert.True(!body.TryGetProperty("schemaDiff", out var schemaDiff) || schemaDiff.ValueKind == JsonValueKind.Object);
        Assert.True(!body.TryGetProperty("correlationMatrix", out var correlation) || correlation.ValueKind == JsonValueKind.Object);
    }

    [Fact]
    public async Task JobResult_ReturnsPersistedAnalysisResult()
    {
        var jobId = await RunAsyncAnalysisAsync();
        var response = await CreateClient().GetAsync($"/api/analysis/jobs/{jobId}/result");

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(3, body.GetProperty("totalObjects").GetInt32());
        Assert.True(body.TryGetProperty("fieldStatistics", out _));
    }

    [Fact]
    public async Task QualityFieldsResponse_ReturnsWebRequiredCollections()
    {
        var jobId = await RunAsyncAnalysisAsync();
        var response = await CreateClient().GetAsync($"/api/quality/fields/{jobId}");

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(body.TryGetProperty("fields", out var fields));
        Assert.True(body.TryGetProperty("normalizedFields", out var normalizedFields));
        Assert.True(body.TryGetProperty("issues", out var issues));
        Assert.Equal(JsonValueKind.Array, fields.ValueKind);
        Assert.Equal(JsonValueKind.Array, normalizedFields.ValueKind);
        Assert.Equal(JsonValueKind.Array, issues.ValueKind);
        Assert.NotEmpty(fields.EnumerateArray());
    }

    [Fact]
    public async Task AnalysisJobList_ReturnsWebRequiredJobFields()
    {
        var jobId = await RunAsyncAnalysisAsync();
        var response = await CreateClient().GetAsync("/api/analysis/jobs?limit=10");

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var jobs = await response.Content.ReadFromJsonAsync<JsonElement>();
        var job = jobs.EnumerateArray().First(x => x.GetProperty("id").GetGuid() == jobId);

        Assert.True(job.TryGetProperty("startedAtUtc", out _));
        Assert.True(job.TryGetProperty("status", out _));
        Assert.True(job.TryGetProperty("totalObjects", out _));
        Assert.True(job.TryGetProperty("fieldCount", out _));
        Assert.True(job.TryGetProperty("qualityScore", out _));
        Assert.True(job.TryGetProperty("issueCount", out _));
        Assert.True(job.TryGetProperty("durationMs", out _));
    }

    [Fact]
    public async Task QualityRelationships_ReturnsWebCompatibleResponse()
    {
        var jobId = await RunAsyncAnalysisAsync();
        var response = await CreateClient().GetAsync($"/api/quality/relationships/{jobId}");

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var body = await ReadJsonOrNullAsync(response);
        Assert.True(body is null || body.Value.ValueKind == JsonValueKind.Object);
    }

    [Fact]
    public async Task QualityObjectRelationships_ReturnsWebCompatibleArray()
    {
        var jobId = await RunAsyncAnalysisAsync();
        var response = await CreateClient().GetAsync($"/api/quality/object-relationships/{jobId}");

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var body = await ReadJsonOrNullAsync(response);
        Assert.True(body is null || body.Value.ValueKind == JsonValueKind.Array);
    }

    [Fact]
    public async Task QualitySchemaImpact_ReturnsWebCompatibleResponse()
    {
        var jobId = await RunAsyncAnalysisAsync();
        var response = await CreateClient().GetAsync($"/api/quality/schema-impact/{jobId}");

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var body = await ReadJsonOrNullAsync(response);
        Assert.True(body is null || body.Value.ValueKind == JsonValueKind.Object);
    }

    private static async Task<JsonElement?> ReadJsonOrNullAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(text))
            return null;

        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private async Task<Guid> RunAsyncAnalysisAsync()
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/analyze/async/config", new
        {
            dataText = DataJson,
            format = "json"
        });

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var submit = await response.Content.ReadFromJsonAsync<JsonElement>();
        var jobIdText = submit.GetProperty("jobId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(jobIdText));
        var jobId = Guid.Parse(jobIdText!);

        for (var i = 0; i < 100; i++)
        {
            var statusResponse = await client.GetAsync($"/api/analyze/async/{jobId:N}");
            Assert.True(statusResponse.IsSuccessStatusCode, await statusResponse.Content.ReadAsStringAsync());
            var status = await statusResponse.Content.ReadFromJsonAsync<JsonElement>();
            var statusText = status.GetProperty("status").GetString();

            if (statusText != "Running")
            {
                Assert.Equal("Completed", statusText);

                // Registry 进入 Completed 后，持久化历史可能仍处于最后一个异步写入窗口。
                // 直接等待结果端点可读，比只等待历史记录存在更可靠，因为后续契约测试都依赖 ResultJson。
                for (var j = 0; j < 50; j++)
                {
                    var resultResponse = await client.GetAsync($"/api/analysis/jobs/{jobId}/result");
                    if (resultResponse.IsSuccessStatusCode)
                        return jobId;

                    await Task.Delay(100);
                }

                throw new Xunit.Sdk.XunitException($"Async analysis job {jobId} completed in registry but its persisted result was not available within the test timeout.");
            }

            await Task.Delay(100);
        }

        throw new Xunit.Sdk.XunitException($"Async analysis job {jobId} did not complete within the test timeout.");
    }
}
