using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ObjectAnalyzer.Api.Tests;

/// <summary>
/// 出站打码契约测试：分析端点的响应（结果 + 数据预览）默认已按 PII 策略打码；
/// piiOff=true 显式关闭时返回原始值并以 piiRedacted=false 留痕。
/// </summary>
public class PiiRedactionEndpointTests : IClassFixture<IsolatedApiWebApplicationFactory>
{
    private readonly IsolatedApiWebApplicationFactory _factory;

    public PiiRedactionEndpointTests(IsolatedApiWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private const string PiiPayload = """[{"Phone":"13812341234","City":"Beijing"},{"Phone":"13900001111","City":"Shanghai"}]""";

    private static JsonElement FieldStat(JsonElement body, string fieldName)
    {
        foreach (var stat in body.GetProperty("result").GetProperty("fieldStatistics").EnumerateArray())
        {
            if (stat.GetProperty("fieldName").GetString() == fieldName)
                return stat;
        }

        throw new InvalidOperationException($"fieldStatistics 中未找到字段 {fieldName}");
    }

    [Fact]
    public async Task AnalyzeRaw_Default_MasksResultAndPreview()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsync("/api/analyze/raw?format=json",
            new StringContent(PiiPayload, Encoding.UTF8, "application/json"));

        Assert.True(response.IsSuccessStatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // 打码标记：默认生效
        Assert.True(body.GetProperty("piiRedacted").GetBoolean());

        // 结果值分布已打码
        var phone = FieldStat(body, "Phone");
        Assert.Contains("138****1234", phone.GetProperty("valueCounts").EnumerateObject().Select(p => p.Name));
        Assert.DoesNotContain("13812341234", phone.GetProperty("valueCounts").EnumerateObject().Select(p => p.Name));

        // 数据预览同样打码（这是此前 API 路径完全缺失的覆盖）
        var previewPhone = body.GetProperty("preview").GetProperty("rows")[0].GetProperty("Phone").GetString();
        Assert.Equal("138****1234", previewPhone);

        // PII 打码视图：识别出的敏感字段及其解析后的打码元数据（label 与 CLI --pii-report 同源）
        var pii = body.GetProperty("pii");
        Assert.Equal(1, pii.GetProperty("fieldCount").GetInt32());
        var piiPhone = pii.GetProperty("fields").EnumerateArray()
            .First(f => f.GetProperty("field").GetString() == "Phone");
        Assert.Equal("Phone", piiPhone.GetProperty("category").GetString());
        Assert.Equal("手机号", piiPhone.GetProperty("label").GetString());
        Assert.Equal("*", piiPhone.GetProperty("maskChar").GetString());
        Assert.False(piiPhone.GetProperty("isCustom").GetBoolean());
        Assert.True(piiPhone.GetProperty("highSeverity").GetBoolean());
    }

    [Fact]
    public async Task AnalyzeRaw_PiiOff_ReturnsRawValuesWithFlag()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsync("/api/analyze/raw?format=json&piiOff=true",
            new StringContent(PiiPayload, Encoding.UTF8, "application/json"));

        Assert.True(response.IsSuccessStatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // 显式退出留痕：piiRedacted=false
        Assert.False(body.GetProperty("piiRedacted").GetBoolean());

        var phone = FieldStat(body, "Phone");
        Assert.Contains("13812341234", phone.GetProperty("valueCounts").EnumerateObject().Select(p => p.Name));
        Assert.DoesNotContain("138****1234", phone.GetProperty("valueCounts").EnumerateObject().Select(p => p.Name));

        var previewPhone = body.GetProperty("preview").GetProperty("rows")[0].GetProperty("Phone").GetString();
        Assert.Equal("13812341234", previewPhone);

        // 元数据照常可见：piiOff 只关打码，不影响敏感字段识别结果
        Assert.Equal(1, body.GetProperty("pii").GetProperty("fieldCount").GetInt32());
    }

    [Fact]
    public async Task AnalyzeRaw_NoPii_PiiViewEmpty()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsync("/api/analyze/raw?format=json",
            new StringContent("""[{"id":1,"active":true}]""", Encoding.UTF8, "application/json"));

        Assert.True(response.IsSuccessStatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(0, body.GetProperty("pii").GetProperty("fieldCount").GetInt32());
        Assert.Empty(body.GetProperty("pii").GetProperty("fields").EnumerateArray());
    }

    [Fact]
    public async Task AnalyzeConfig_Default_MasksCustomRuleField()
    {
        var client = _factory.CreateClient();
        var requestBody = """
            {
              "dataText": "[{\"internalId\":\"ID-1001\"},{\"internalId\":\"ID-2002\"}]",
              "format": "json",
              "rules": {
                "fields": {
                  "internalId": { "pii": { "label": "内部标识", "mask": { "keepPrefix": 2, "keepSuffix": 2 } } }
                }
              }
            }
            """;
        var response = await client.PostAsync("/api/analyze/config",
            new StringContent(requestBody, Encoding.UTF8, "application/json"));

        Assert.True(response.IsSuccessStatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(body.GetProperty("piiRedacted").GetBoolean());
        var internalId = FieldStat(body, "internalId");
        Assert.Contains("ID***01", internalId.GetProperty("valueCounts").EnumerateObject().Select(p => p.Name));
        Assert.DoesNotContain("ID-1001", internalId.GetProperty("valueCounts").EnumerateObject().Select(p => p.Name));

        // 规则声明的自定义敏感字段：label / isCustom / keep 前后缀来自规则解析
        var piiInternalId = body.GetProperty("pii").GetProperty("fields").EnumerateArray()
            .First(f => f.GetProperty("field").GetString() == "internalId");
        Assert.Equal("内部标识", piiInternalId.GetProperty("label").GetString());
        Assert.Equal("Custom", piiInternalId.GetProperty("category").GetString());
        Assert.True(piiInternalId.GetProperty("isCustom").GetBoolean());
        Assert.Equal(2, piiInternalId.GetProperty("keepPrefix").GetInt32());
        Assert.Equal(2, piiInternalId.GetProperty("keepSuffix").GetInt32());
    }

    [Fact]
    public async Task AnalyzeRaw_PiiOff_WritesAuditEntryWhenConfigured()
    {
        var client = _factory.CreateClient();
        string auditPath = Path.ChangeExtension(Path.GetTempFileName(), ".ndjson");
        var rules = new { runtime = new { audit = new { log = auditPath } } };
        var url = "/api/analyze/raw?format=json&piiOff=true&rulesJson=" + Uri.EscapeDataString(JsonSerializer.Serialize(rules));
        try
        {
            var response = await client.PostAsync(url,
                new StringContent(PiiPayload, Encoding.UTF8, "application/json"));
            Assert.True(response.IsSuccessStatusCode);
            Assert.False((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("piiRedacted").GetBoolean());

            // piiOff 请求在配置了审计路径时落结构化审计（事件 pii_off，不含字段值）
            Assert.True(File.Exists(auditPath), "piiOff 审计文件未生成");
            string audit = File.ReadAllText(auditPath);
            Assert.Contains("\"event\":\"pii_off\"", audit);
            Assert.DoesNotContain("13812341234", audit);
        }
        finally
        {
            File.Delete(auditPath);
        }
    }

    [Fact]
    public async Task AnalyzeAsync_Default_JobResultIsMasked()
    {
        var client = _factory.CreateClient();
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(PiiPayload, Encoding.UTF8, "application/json"), "File", "data.json");

        var submit = await client.PostAsync("/api/analyze/async", form);
        Assert.True(submit.IsSuccessStatusCode);
        var submitBody = await submit.Content.ReadFromJsonAsync<JsonElement>();
        var jobId = submitBody.GetProperty("jobId").GetString()!;

        // 轮询至作业完成（结果在终态前为 null）
        JsonElement body = default;
        for (int i = 0; i < 30; i++)
        {
            await Task.Delay(300);
            var poll = await client.GetAsync($"/api/analyze/async/{jobId}");
            Assert.True(poll.IsSuccessStatusCode);
            body = await poll.Content.ReadFromJsonAsync<JsonElement>();
            if (body.GetProperty("result").ValueKind != JsonValueKind.Null)
                break;
        }

        Assert.NotEqual(JsonValueKind.Null, body.GetProperty("result").ValueKind);
        Assert.True(body.GetProperty("piiRedacted").GetBoolean());
        Assert.Equal(1, body.GetProperty("pii").GetProperty("fieldCount").GetInt32());

        var phone = FieldStat(body, "Phone");
        Assert.Contains("138****1234", phone.GetProperty("valueCounts").EnumerateObject().Select(p => p.Name));
        // 异步快照的 preview 是裸行数组（与同步端点的 {columns, rows} 包装视图不同，既有契约如此）
        Assert.Equal("138****1234", body.GetProperty("preview")[0].GetProperty("Phone").GetString());
    }
}
