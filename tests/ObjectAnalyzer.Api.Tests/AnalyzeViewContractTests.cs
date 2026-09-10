using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ObjectAnalyzer.Api.Tests;

/// <summary>
/// W8 v2 契约验收：五页面视图响应（概览 / 预览 / 字段 / 深度分析 / 质量）+ 异步作业进度与 SSE 事件流。
/// React 前端按本组测试锁定的形状消费。
/// </summary>
public class AnalyzeViewContractTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public AnalyzeViewContractTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private HttpClient CreateClient() => _factory.CreateClient();

    private static readonly string[] s_expectedPreviewColumns = ["name", "age"];

    private const string DataJson = """[{"name":"alice","age":30},{"name":"bob","age":25},{"name":"carol","age":41}]""";
    private const string RulesJson = """{"fields":{"age":{"primaryType":"Number","minValue":26}},"runtime":{"gates":{"failOnViolations":true}}}""";

    [Fact]
    public async Task AnalyzeRaw_ViewResponse_HasFivePageSections()
    {
        var client = CreateClient();
        var url = "/api/analyze/raw?format=json&rulesJson=" + Uri.EscapeDataString(RulesJson);
        var response = await client.PostAsync(url, new StringContent(DataJson, System.Text.Encoding.UTF8, "application/json"));

        Assert.True(response.IsSuccessStatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // 顶层：格式 / 规则来源 / 完整结果 兼容保留
        Assert.Equal("json", body.GetProperty("format").GetString());
        Assert.Equal("inline", body.GetProperty("rulesSource").GetString());
        Assert.True(body.TryGetProperty("result", out var result));
        Assert.Equal(3, result.GetProperty("totalObjects").GetInt32());

        // 概览统计页
        var overview = body.GetProperty("overview");
        Assert.Equal(3, overview.GetProperty("totalObjects").GetInt32());
        Assert.Equal(2, overview.GetProperty("fieldCount").GetInt32());
        Assert.Equal(1, overview.GetProperty("violationCount").GetInt32());

        // 字段明细页
        var fields = body.GetProperty("fields");
        Assert.Equal(2, fields.GetArrayLength());
        Assert.Contains(fields.EnumerateArray(), f => f.GetProperty("fieldName").GetString() == "age");

        // 深度分析页：未启用深度分析时键整体省略（API 契约为省略 null，前端以 TryGetProperty 判断）
        Assert.False(body.TryGetProperty("deepAnalysis", out _));

        // 质量报告页：1 条范围违规 + 按类型计数
        var quality = body.GetProperty("quality");
        Assert.True(quality.GetProperty("hasViolations").GetBoolean());
        Assert.Equal(1, quality.GetProperty("countsByCheck").GetProperty("range").GetInt32());
        Assert.Equal(1, quality.GetProperty("violations").GetArrayLength());

        // 质量门禁页：必须使用本次请求 rules.runtime.gates 的实际配置，不能回退到默认 AnalysisOptions
        var gates = quality.GetProperty("gates");
        Assert.NotEmpty(gates.EnumerateArray());
        var qualityGate = gates.EnumerateArray().First(g => g.GetProperty("exitCode").GetInt32() == 3);
        Assert.True(qualityGate.GetProperty("triggered").GetBoolean());
        Assert.True(quality.GetProperty("hasGateFailures").GetBoolean());
    }

    [Fact]
    public async Task AnalyzeMultipart_ViewResponse_ContainsPreview()
    {
        var client = CreateClient();

        using var content = new MultipartFormDataContent();
        var bytes = System.Text.Encoding.UTF8.GetBytes(DataJson);
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Add(fileContent, "file", "people.json");

        var response = await client.PostAsync("/api/analyze", content);
        Assert.True(response.IsSuccessStatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("people.json", body.GetProperty("fileName").GetString());

        // 数据预览页：列按首次出现顺序；PII 字段（name 命中姓名语义）已按出站策略打码，
        // 非 PII 字段保持原始类型
        var preview = body.GetProperty("preview");
        var columns = preview.GetProperty("columns").EnumerateArray().Select(c => c.GetString()).ToList();
        Assert.Equal(s_expectedPreviewColumns, columns);
        var rows = preview.GetProperty("rows");
        Assert.Equal(3, rows.GetArrayLength());
        Assert.Equal("a***e", rows[0].GetProperty("name").GetString());
        Assert.Equal(30, rows[0].GetProperty("age").GetInt32());
        Assert.False(preview.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task AnalyzeMultipart_ViewResponse_UsesRuntimeGateOptions()
    {
        var client = CreateClient();

        using var content = new MultipartFormDataContent();
        var bytes = System.Text.Encoding.UTF8.GetBytes(DataJson);
        content.Add(new ByteArrayContent(bytes), "file", "people.json");
        content.Add(new StringContent(RulesJson, System.Text.Encoding.UTF8, "application/json"), "rulesJson");

        var response = await client.PostAsync("/api/analyze", content);
        Assert.True(response.IsSuccessStatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var quality = body.GetProperty("quality");
        var gates = quality.GetProperty("gates");
        var qualityGate = gates.EnumerateArray().First(g => g.GetProperty("exitCode").GetInt32() == 3);

        Assert.True(qualityGate.GetProperty("triggered").GetBoolean());
        Assert.True(quality.GetProperty("hasGateFailures").GetBoolean());
    }

    [Fact]
    public async Task AnalyzeConfig_ViewResponse_UsesRuntimeGateOptions()
    {
        var client = CreateClient();
        var request = new
        {
            dataText = DataJson,
            format = "json",
            rules = new
            {
                fields = new
                {
                    age = new
                    {
                        primaryType = "Number",
                        minValue = 26
                    }
                },
                runtime = new
                {
                    gates = new
                    {
                        failOnViolations = true
                    }
                }
            }
        };

        var response = await client.PostAsJsonAsync("/api/analyze/config", request);
        Assert.True(response.IsSuccessStatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var quality = body.GetProperty("quality");
        var gates = quality.GetProperty("gates");
        var qualityGate = gates.EnumerateArray().First(g => g.GetProperty("exitCode").GetInt32() == 3);

        Assert.True(qualityGate.GetProperty("triggered").GetBoolean());
        Assert.True(quality.GetProperty("hasGateFailures").GetBoolean());
    }

    [Fact]
    public async Task AnalyzeRaw_WithoutPreviewFields_ReturnsEmptyPreview()
    {
        var client = CreateClient();
        var response = await client.PostAsync("/api/analyze/raw?format=json",
            new StringContent("[]", System.Text.Encoding.UTF8, "application/json"));

        Assert.True(response.IsSuccessStatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var preview = body.GetProperty("preview");
        Assert.Equal(0, preview.GetProperty("rows").GetArrayLength());
        Assert.False(preview.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task AsyncJob_StatusCarriesProcessedObjects_AndSseReachesComplete()
    {
        var client = CreateClient();

        // 提交异步作业（inline config 端点）
        var submitBody = new Dictionary<string, object?>
        {
            ["dataText"] = DataJson,
            ["format"] = "json"
        };
        var submitResponse = await client.PostAsJsonAsync("/api/analyze/async/config", submitBody);
        Assert.True(submitResponse.IsSuccessStatusCode);
        var submit = await submitResponse.Content.ReadFromJsonAsync<JsonElement>();
        var jobId = submit.GetProperty("jobId").GetString()!;

        // 轮询状态端点直到终态（同时验证 processedObjects 字段在契约中）
        JsonElement snapshot = default;
        for (int i = 0; i < 100; i++)
        {
            var snapResponse = await client.GetAsync($"/api/analyze/async/{jobId}");
            Assert.True(snapResponse.IsSuccessStatusCode);
            snapshot = await snapResponse.Content.ReadFromJsonAsync<JsonElement>();
            if (snapshot.GetProperty("status").GetString() != "Running")
                break;
            await Task.Delay(100);
        }

        Assert.Equal("Completed", snapshot.GetProperty("status").GetString());
        Assert.True(snapshot.TryGetProperty("processedObjects", out _));
        // W8：终态快照带预览样本（快照上的 preview 为原始行数组；列可由行键推导）
        Assert.True(snapshot.TryGetProperty("preview", out var preview));
        Assert.Equal(3, preview.GetArrayLength());

        // SSE：对已终态的作业订阅事件流 → 服务端发完 complete 事件即关闭流
        var sseResponse = await client.GetAsync($"/api/analyze/async/{jobId}/events");
        Assert.True(sseResponse.IsSuccessStatusCode);
        Assert.Equal("text/event-stream", sseResponse.Content.Headers.ContentType!.MediaType);

        string stream = await sseResponse.Content.ReadAsStringAsync();
        Assert.Contains("event: complete", stream);
        Assert.Contains("\"status\":\"Completed\"", stream);

        // complete 事件的 data 行是合法 JSON，processedObjects 字段在
        var dataLine = stream.Split('\n').First(l => l.StartsWith("data: ", StringComparison.Ordinal));
        var payload = JsonSerializer.Deserialize<JsonElement>(dataLine["data: ".Length..]);
        Assert.Equal("Completed", payload.GetProperty("status").GetString());
        Assert.Equal(3, payload.GetProperty("processedObjects").GetInt32());
    }

    [Fact]
    public async Task AsyncJob_SseForUnknownJob_GetsGoneEvent()
    {
        var client = CreateClient();
        var response = await client.GetAsync("/api/analyze/async/nonexistent-job/events");

        Assert.True(response.IsSuccessStatusCode);
        string stream = await response.Content.ReadAsStringAsync();
        Assert.Contains("event: gone", stream);
    }
}
