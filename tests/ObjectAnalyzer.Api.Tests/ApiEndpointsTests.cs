using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ObjectAnalyzer.Api.Tests;

/// <summary>
/// Mini API 集成冒烟测试：健康检查、数据文本分析、multipart 上传分析、规则校验。
/// </summary>
public class ApiEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ApiEndpointsTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    private HttpClient CreateClient() => _factory.CreateClient();

    [Fact]
    public async Task Health_ReturnsOk()
    {
        var client = CreateClient();
        var response = await client.GetAsync("/api/health");

        Assert.True(response.IsSuccessStatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ok", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task AnalyzeRaw_JsonBody_ReturnsCamelCaseResult()
    {
        var client = CreateClient();
        var valid = """[{"id":1,"name":"alice","age":30},{"id":2,"name":"bob","age":45}]""";

        var response = await client.PostAsync("/api/analyze/raw?format=json",
            new StringContent(valid, System.Text.Encoding.UTF8, "application/json"));

        Assert.True(response.IsSuccessStatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, body.GetProperty("result").GetProperty("totalObjects").GetInt32());

        var first = body.GetProperty("result").GetProperty("fieldStatistics")[0];
        Assert.Equal("age", first.GetProperty("fieldName").GetString());
    }

    [Fact]
    public async Task AnalyzeRaw_InvalidJson_Returns400()
    {
        var client = CreateClient();
        var response = await client.PostAsync("/api/analyze/raw?format=json",
            new StringContent("{ not valid", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrEmpty(body.GetProperty("error").GetString()));
    }

    [Fact]
    public async Task Analyze_MultipartUpload_ReturnsResult()
    {
        var client = CreateClient();
        using var form = new MultipartFormDataContent();
        var csv = "name,age\nalice,30\nbob,45\n";
        form.Add(new StringContent(csv, System.Text.Encoding.UTF8, "text/csv"), "file", "people.csv");
        form.Add(new StringContent("30"), "maxValues");

        var response = await client.PostAsync("/api/analyze", form);

        Assert.True(response.IsSuccessStatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("people.csv", body.GetProperty("fileName").GetString());
        // csv 扩展名按既定语义路由为 excel 格式（Excel/CSV 共享同一解析器）
        Assert.Equal(2, body.GetProperty("result").GetProperty("totalObjects").GetInt32());
    }

    [Fact]
    public async Task Analyze_WithoutFile_Returns400()
    {
        var client = CreateClient();
        using var form = new MultipartFormDataContent();

        var response = await client.PostAsync("/api/analyze", form);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RulesValidate_ValidJson_ReturnsValid()
    {
        var client = CreateClient();
        var response = await client.PostAsync("/api/rules/validate",
            new StringContent("""{"fields":{"age":{"primaryType":"Number"}}}""",
                System.Text.Encoding.UTF8, "application/json"));

        Assert.True(response.IsSuccessStatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("valid").GetBoolean());
        Assert.Equal(1, body.GetProperty("fields").GetInt32());
    }

    [Fact]
    public async Task RulesValidate_WithComments_PassesCommentTolerantLoading()
    {
        var client = CreateClient();
        var response = await client.PostAsync("/api/rules/validate",
            new StringContent("{ /* 注释 */ \"fields\": { \"age\": { \"primaryType\": \"Number\" } } }",
                System.Text.Encoding.UTF8, "application/json"));

        Assert.True(response.IsSuccessStatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("valid").GetBoolean());
    }

    [Fact]
    public async Task RulesReference_ReturnsTemplate()
    {
        var client = CreateClient();
        var response = await client.GetAsync("/api/rules/reference");

        Assert.True(response.IsSuccessStatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("fields", body);
    }
}
