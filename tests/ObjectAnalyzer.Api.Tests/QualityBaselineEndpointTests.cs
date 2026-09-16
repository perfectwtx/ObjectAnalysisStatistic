using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using ObjectAnalyzer.Api;

namespace ObjectAnalyzer.Api.Tests;

public sealed class QualityBaselineEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public QualityBaselineEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task QualityBaseline_Crud_Works()
    {
        var client = _factory.CreateClient();
        var sourceName = $"api-test-{Guid.NewGuid():N}";
        var request = new QualityBaselineRequest(sourceName, 91, 92, 93, 94, 95, 96);

        try
        {
            var post = await client.PostAsJsonAsync("/api/quality-baselines", request);
            Assert.Equal(HttpStatusCode.OK, post.StatusCode);
            var created = await post.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(sourceName, created.GetProperty("sourceName").GetString());
            Assert.Equal(91, created.GetProperty("minimumOverallScore").GetDouble());

            var putRequest = request with { MinimumOverallScore = 90 };
            var put = await client.PutAsJsonAsync($"/api/quality-baselines/{sourceName}", putRequest);
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
            var saved = await put.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(90, saved.GetProperty("minimumOverallScore").GetDouble());

            var get = await client.GetAsync($"/api/quality-baselines/{sourceName}");
            Assert.Equal(HttpStatusCode.OK, get.StatusCode);
            var loaded = await get.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(96, loaded.GetProperty("minimumAnomalyControl").GetDouble());

            var all = await client.GetFromJsonAsync<JsonElement>("/api/quality-baselines");
            Assert.Contains(all.EnumerateArray(), item => item.GetProperty("sourceName").GetString() == sourceName);
        }
        finally
        {
            await client.DeleteAsync($"/api/quality-baselines/{sourceName}");
        }

        var missing = await client.GetAsync($"/api/quality-baselines/{sourceName}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task QualityBaseline_InvalidThreshold_Returns400()
    {
        var client = _factory.CreateClient();
        var sourceName = $"api-test-{Guid.NewGuid():N}";
        var request = new QualityBaselineRequest(sourceName, 101);

        var response = await client.PutAsJsonAsync($"/api/quality-baselines/{sourceName}", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("0 到 100", body.GetProperty("error").GetString());
    }
}
