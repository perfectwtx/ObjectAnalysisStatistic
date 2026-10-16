using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ObjectAnalyzer.Api.Tests;

/// <summary>
/// Mini API 集成冒烟测试：健康检查、数据文本分析、multipart 上传分析、规则校验。
/// </summary>
public class ApiEndpointsTests : IClassFixture<IsolatedApiWebApplicationFactory>
{
    private readonly IsolatedApiWebApplicationFactory _factory;

    public ApiEndpointsTests(IsolatedApiWebApplicationFactory factory)
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
    public async Task AnalyzeConfig_UnknownRuleKey_Returns400WithUnknowns()
    {
        // config 端点必须与 multipart / raw 同源防护：强类型绑定会静默丢弃不认识的键
        // （错层级 / 拼错的规则键「看似生效实则无效」），因此读原始体先跑 RulesLint
        var client = CreateClient();
        var body = """{"dataText":"[{\"a\":1}]","format":"json","rules":{"fields":{"a":{"bogusKey":1}}}}""";

        var response = await client.PostAsync("/api/analyze/config",
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("bogusKey", text);
    }

    [Fact]
    public async Task AnalyzeAsyncConfig_UnknownRuleKey_Returns400WithUnknowns()
    {
        var client = CreateClient();
        var body = """{"dataText":"[{\"a\":1}]","format":"json","rules":{"fields":{"a":{"bogusKey":1}}}}""";

        var response = await client.PostAsync("/api/analyze/async/config",
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("bogusKey", text);
    }

    [Fact]
    public async Task AnalyzeConfig_InvalidJsonBody_Returns400()
    {
        var client = CreateClient();
        var response = await client.PostAsync("/api/analyze/config",
            new StringContent("{ not valid", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("JSON", body.GetProperty("error").GetString());
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
    public async Task RulesValidate_UnknownKey_FailsByDefault()
    {
        var client = CreateClient();
        // minValue2 是拼错的键：STJ 反序列化时会静默忽略，校验接口必须把它揪出来
        var response = await client.PostAsync("/api/rules/validate",
            new StringContent("""{"fields":{"age":{"minValue2":0,"required":true}}}""",
                System.Text.Encoding.UTF8, "application/json"));

        Assert.True(response.IsSuccessStatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("valid").GetBoolean());

        var unknowns = body.GetProperty("unknowns").EnumerateArray().ToList();
        var hit = Assert.Single(unknowns);
        Assert.Equal("fields.age", hit.GetProperty("path").GetString());
        Assert.Equal("minValue2", hit.GetProperty("name").GetString());
        Assert.Equal("minValue", hit.GetProperty("suggestion").GetString());

        var error = body.GetProperty("error").GetString();
        Assert.Contains("minValue2", error);
        Assert.Contains("minValue", error);
    }

    [Fact]
    public async Task RulesValidate_UnknownKey_WithStrictFalse_Passes()
    {
        var client = CreateClient();
        var response = await client.PostAsync("/api/rules/validate?strict=false",
            new StringContent("""{"fields":{"age":{"minValue2":0}}}""",
                System.Text.Encoding.UTF8, "application/json"));

        Assert.True(response.IsSuccessStatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("valid").GetBoolean());
        // 仍然报出来，只是不判失败
        Assert.Equal(1, body.GetProperty("unknowns").GetArrayLength());
    }

    [Fact]
    public async Task RulesValidate_RealReferenceTemplate_HasNoUnknowns()
    {
        var client = CreateClient();
        var template = await client.GetStringAsync("/api/rules/reference");

        var response = await client.PostAsync("/api/rules/validate",
            new StringContent(template, System.Text.Encoding.UTF8, "application/json"));

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        // 参考模板覆盖了全部规则键，若有未知项说明反射取属性名取错了（会误伤所有用户）
        Assert.True(body.GetProperty("valid").GetBoolean(),
            JsonSerializer.Serialize(body.GetProperty("unknowns")));
        Assert.Empty(body.GetProperty("unknowns").EnumerateArray());
    }

    [Fact]
    public async Task Analyze_UnknownRuleKey_Returns400()
    {
        var client = CreateClient();
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("name,age\nalice,30\n", System.Text.Encoding.UTF8, "text/csv"),
            "file", "people.csv");
        form.Add(new StringContent("""{"fields":{"age":{"requird":true}}}""",
            System.Text.Encoding.UTF8, "application/json"), "rulesJson");

        var response = await client.PostAsync("/api/analyze", form);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("requird", body.GetProperty("error").GetString());
        Assert.Equal("required",
            body.GetProperty("unknowns")[0].GetProperty("suggestion").GetString());
    }

    [Fact]
    public async Task Analyze_UnknownRuleKey_AllowedByFlag_ReturnsOk()
    {
        var client = CreateClient();
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("name,age\nalice,30\n", System.Text.Encoding.UTF8, "text/csv"),
            "file", "people.csv");
        form.Add(new StringContent("""{"fields":{"age":{"requird":true}}}""",
            System.Text.Encoding.UTF8, "application/json"), "rulesJson");
        form.Add(new StringContent("true"), "allowUnknownRules");

        var response = await client.PostAsync("/api/analyze", form);

        Assert.True(response.IsSuccessStatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("result").GetProperty("totalObjects").GetInt32());
    }

    [Fact]
    public async Task Analyze_ValueParsersRule_ForcesExcelJsonParsing()
    {
        // 回归：规则里配了 valueParsers 时，API 必须和 CLI 一样切到 excel-json 解析器。
        // 之前 API 直接拿扩展名检测出的格式（csv → excel）去解析，单元格只被当成纯文本读出，
        // 于是字段还叫 "A"，"A.body" 上的 transform 永远匹配不上，JSON 值一个都展不开。
        var client = CreateClient();

        // 单元格内容：{"body":"{\"mountain\":\"bear\"}"}（放进 CSV 时引号需翻倍）
        string cell = "{\"body\":\"{\\\"mountain\\\":\\\"bear\\\"}\"}";
        string csv = "A\n\"" + cell.Replace("\"", "\"\"") + "\"\n";

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(csv, System.Text.Encoding.UTF8, "text/csv"), "file", "cell.csv");
        form.Add(
            new StringContent(
                """{"runtime":{"flatten":true},"valueParsers":[{"source":"A","parseType":"json","header":1}],"fields":{"A.body":{"transform":{"parse":"json"}}}}""",
                System.Text.Encoding.UTF8, "application/json"),
            "rulesJson");

        var response = await client.PostAsync("/api/analyze", form);

        Assert.True(response.IsSuccessStatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        // 响应如实回写实际使用的解析器，便于前端判断规则是否真的生效
        Assert.Equal("excel-json", body.GetProperty("format").GetString());

        var stats = body.GetProperty("result").GetProperty("fieldStatistics").EnumerateArray().ToList();
        var names = stats.Select(f => f.GetProperty("fieldName").GetString()).ToList();

        Assert.Contains("A.body.mountain", names);
        // 没生效时只会剩一个原始文本列 A
        Assert.DoesNotContain("A", names);

        var mountain = stats.Single(f => f.GetProperty("fieldName").GetString() == "A.body.mountain");
        Assert.Equal("bear", mountain.GetProperty("valueCounts").EnumerateObject().Single().Name);
    }

    [Fact]
    public async Task AnalyzeRaw_WithRulesJson_AppliesValueParsers()
    {
        // /api/analyze/raw 原先硬编码传 rules: null，粘贴/直连场景用不了 valueParsers 与 transform。
        // 请求体被数据文本占用，规则只能走查询参数，这里验证它确实生效。
        var client = CreateClient();

        // 用最小输入隔离验证 valueParsers 本身，避免同时测试嵌套字符串 transform 造成失败原因混杂。
        string cell = "{\"mountain\":\"bear\"}";
        string csv = "A\n\"" + cell.Replace("\"", "\"\"") + "\"\n";
        string rules = """
            {"runtime":{"flatten":true},"valueParsers":[{"source":"A","parseType":"json","header":1}]}
            """.Replace("\n", "").Replace("\r", "");

        var url = "/api/analyze/raw?format=csv&rulesJson=" + Uri.EscapeDataString(rules);
        var response = await client.PostAsync(url,
            new StringContent(csv, System.Text.Encoding.UTF8, "text/plain"));

        Assert.True(response.IsSuccessStatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("excel-json", body.GetProperty("format").GetString());
        Assert.Equal("inline", body.GetProperty("rulesSource").GetString());

        var names = body.GetProperty("result").GetProperty("fieldStatistics")
            .EnumerateArray()
            .Select(f => f.GetProperty("fieldName").GetString())
            .ToList();
        Assert.Contains("A.mountain", names);
    }

    [Fact]
    public async Task AnalyzeRaw_UnknownRuleKey_Returns400()
    {
        var client = CreateClient();
        var rules = """{"fields":{"age":{"requird":true}}}""";

        var response = await client.PostAsync(
            "/api/analyze/raw?format=json&rulesJson=" + Uri.EscapeDataString(rules),
            new StringContent("""[{"age":30}]""", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("requird", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task AnalyzeRaw_WithoutRules_OmitsRulesSource()
    {
        // 不传规则时响应里不该多出 rulesSource，避免前端把「没规则」误判成「规则没生效」
        var client = CreateClient();

        var response = await client.PostAsync("/api/analyze/raw?format=json",
            new StringContent("""[{"age":30}]""", System.Text.Encoding.UTF8, "application/json"));

        Assert.True(response.IsSuccessStatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.TryGetProperty("rulesSource", out _));
        Assert.Equal("json", body.GetProperty("format").GetString());
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

    // ──────────────────────────────────────────────
    // 预检端点（/api/preflight）：与 Core 的 PreflightChecker 同一份实现
    // ──────────────────────────────────────────────

    [Fact]
    public async Task Preflight_NoRules_ReturnsNoWarning()
    {
        var client = CreateClient();
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("name,age\nalice,30\n", System.Text.Encoding.UTF8, "text/csv"),
            "file", "people.csv");

        var response = await client.PostAsync("/api/preflight", form);

        Assert.True(response.IsSuccessStatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("preflight").GetProperty("hasWarning").GetBoolean());
    }

    [Fact]
    public async Task Preflight_MatchingRules_ReturnsNoWarning()
    {
        var client = CreateClient();
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("name,age\nalice,30\n", System.Text.Encoding.UTF8, "text/csv"),
            "file", "people.csv");
        form.Add(new StringContent("""{"fields":{"name":{},"age":{}}}""",
            System.Text.Encoding.UTF8, "application/json"), "rulesJson");

        var response = await client.PostAsync("/api/preflight", form);

        Assert.True(response.IsSuccessStatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("preflight").GetProperty("hasWarning").GetBoolean());
        Assert.Equal(2, body.GetProperty("preflight").GetProperty("matchedRuleFields").GetArrayLength());
    }

    [Fact]
    public async Task Preflight_NoOverlap_ReturnsWarning()
    {
        var client = CreateClient();
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("name,age\nalice,30\n", System.Text.Encoding.UTF8, "text/csv"),
            "file", "people.csv");
        form.Add(new StringContent("""{"fields":{"line":{},"line.body":{}}}""",
            System.Text.Encoding.UTF8, "application/json"), "rulesJson");

        var response = await client.PostAsync("/api/preflight", form);

        Assert.True(response.IsSuccessStatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("preflight").GetProperty("hasWarning").GetBoolean());
        Assert.Equal(2, body.GetProperty("preflight").GetProperty("unmatchedRuleFields").GetArrayLength());
    }

    [Fact]
    public async Task Preflight_WithoutFile_Returns400()
    {
        var client = CreateClient();
        using var form = new MultipartFormDataContent();

        var response = await client.PostAsync("/api/preflight", form);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Preflight_UnknownRuleKey_Returns400()
    {
        var client = CreateClient();
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("name,age\nalice,30\n", System.Text.Encoding.UTF8, "text/csv"),
            "file", "people.csv");
        form.Add(new StringContent("""{"fields":{"name":{"requird":true}}}""",
            System.Text.Encoding.UTF8, "application/json"), "rulesJson");

        var response = await client.PostAsync("/api/preflight", form);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("requird", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task PreflightConfig_InlineDataText_RespectsRules()
    {
        var client = CreateClient();
        var config = new
        {
            dataText = "{\"name\":\"alice\",\"age\":30}",
            format = "json",
            rulesJson = """{"fields":{"name":{},"age":{}}}"""
        };
        var response = await client.PostAsync("/api/preflight/config",
            new StringContent(JsonSerializer.Serialize(config), System.Text.Encoding.UTF8, "application/json"));

        Assert.True(response.IsSuccessStatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("preflight").GetProperty("hasWarning").GetBoolean());
    }

    // ──────────────────────────────────────────────
    // 异步分析端点：/api/analyze/async + GET /cancel
    // ──────────────────────────────────────────────

    [Fact]
    public async Task AnalyzeAsync_Submit_ReturnsJobId()
    {
        var client = CreateClient();
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("name,age\nalice,30\nbob,45\n", System.Text.Encoding.UTF8, "text/csv"),
            "file", "people.csv");

        var response = await client.PostAsync("/api/analyze/async", form);

        Assert.True(response.IsSuccessStatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        string? jobId = body.GetProperty("jobId").GetString();
        Assert.NotNull(jobId);
        Assert.Equal(32, jobId!.Length);
        Assert.Equal("people.csv", body.GetProperty("fileName").GetString());
        Assert.Equal("Running", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task AnalyzeAsync_Get_NonExistentJob_Returns404()
    {
        var client = CreateClient();
        var response = await client.GetAsync("/api/analyze/async/nonexistent-job-id");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AnalyzeAsync_Get_ExistingJob_ReturnsSnapshot()
    {
        // 提交 → 立即 GET → 验证快照里有 Running 状态和 meta
        var client = CreateClient();
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("name,age\nalice,30\n", System.Text.Encoding.UTF8, "text/csv"),
            "file", "people.csv");

        var submit = await client.PostAsync("/api/analyze/async", form);
        Assert.True(submit.IsSuccessStatusCode, await submit.Content.ReadAsStringAsync());
        var submitBody = await submit.Content.ReadFromJsonAsync<JsonElement>();
        string? jobId = submitBody.GetProperty("jobId").GetString();
        Assert.NotNull(jobId);

        // 第一次 GET（作业可能还在跑，也可能已 Completed）
        var snap = await client.GetAsync($"/api/analyze/async/{jobId}");
        Assert.True(snap.IsSuccessStatusCode,
            $"status={(int)snap.StatusCode} body={await snap.Content.ReadAsStringAsync()}");
        var snapBody = await snap.Content.ReadFromJsonAsync<JsonElement>();
        string? status = snapBody.GetProperty("status").GetString();
        Assert.True(status is "Running" or "Completed" or "Failed" or "Cancelled",
            $"unexpected status: {status}");
    }

    [Fact]
    public async Task AnalyzeAsync_Cancel_NonExistentJob_Returns404()
    {
        var client = CreateClient();
        var response = await client.PostAsync("/api/analyze/async/nonexistent-job-id/cancel", null);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AnalyzeAsync_Cancel_Twice_SecondCallReturnsFalse()
    {
        // 即使作业已 Completed，cancel 端点也不报错，但第二次必须返回 cancelled=false
        // （registry 的 Cancel 仅对非终态作业返回 true；用于验证终态语义）
        var client = CreateClient();
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("name,age\nalice,30\n", System.Text.Encoding.UTF8, "text/csv"),
            "file", "people.csv");

        var submit = await client.PostAsync("/api/analyze/async", form);
        Assert.True(submit.IsSuccessStatusCode);
        string? jobId = (await submit.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("jobId").GetString();
        Assert.NotNull(jobId);

        // 等到终态
        for (int i = 0; i < 50; i++)
        {
            await Task.Delay(100);
            var snap = await (await client.GetAsync($"/api/analyze/async/{jobId}"))
                .Content.ReadFromJsonAsync<JsonElement>();
            string? status = snap.GetProperty("status").GetString();
            if (status is "Completed" or "Failed" or "Cancelled")
            {
                // 已终态：cancel 应返回 cancelled=false
                var cancel = await client.PostAsync($"/api/analyze/async/{jobId}/cancel", null);
                Assert.True(cancel.IsSuccessStatusCode);
                var body = await cancel.Content.ReadFromJsonAsync<JsonElement>();
                Assert.False(body.GetProperty("cancelled").GetBoolean());
                return;
            }
        }
        Assert.Fail("Job did not reach terminal state in time");
    }

    [Fact]
    public async Task AnalyzeAsync_WithoutFile_Returns400()
    {
        var client = CreateClient();
        using var form = new MultipartFormDataContent();

        var response = await client.PostAsync("/api/analyze/async", form);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AnalyzeAsync_SubmitThenPoll_ReachesCompleted()
    {
        // 提交 → 轮询 GET → 最终 Completed + Result 完整
        var client = CreateClient();
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("id,name,age\n1,alice,30\n2,bob,45\n", System.Text.Encoding.UTF8, "text/csv"),
            "file", "people.csv");

        var submit = await client.PostAsync("/api/analyze/async", form);
        Assert.True(submit.IsSuccessStatusCode);
        string? jobId = (await submit.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("jobId").GetString();
        Assert.NotNull(jobId);

        // 轮询直到终态（最多 5 秒）
        for (int i = 0; i < 50; i++)
        {
            await Task.Delay(100);
            var poll = await client.GetAsync($"/api/analyze/async/{jobId}");
            Assert.True(poll.IsSuccessStatusCode,
                $"status={(int)poll.StatusCode} body={await poll.Content.ReadAsStringAsync()}");
            var snap = await poll.Content.ReadFromJsonAsync<JsonElement>();
            string? status = snap.GetProperty("status").GetString();
            if (status is "Completed" or "Failed" or "Cancelled")
            {
                string? errorMsg = null;
                if (snap.TryGetProperty("error", out var errProp) && errProp.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    errorMsg = errProp.GetString();
                }
                if (status != "Completed")
                {
                    throw new Xunit.Sdk.XunitException(
                        $"Expected Completed but got {status}. error={errorMsg}");
                }
                Assert.Equal(2, snap.GetProperty("result").GetProperty("totalObjects").GetInt32());
                return;
            }
        }
        Assert.Fail("Async analysis did not reach terminal state in time");
    }

    [Fact]
    public async Task AnalyzeAsyncConfig_InlineDataText_RunsToCompletion()
    {
        var client = CreateClient();
        var config = new
        {
            dataText = """[{"id":1,"name":"alice","age":30},{"id":2,"name":"bob","age":45}]""",
            format = "json"
        };
        var response = await client.PostAsync("/api/analyze/async/config",
            new StringContent(JsonSerializer.Serialize(config), System.Text.Encoding.UTF8, "application/json"));

        Assert.True(response.IsSuccessStatusCode);
        string? jobId = (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("jobId").GetString();
        Assert.NotNull(jobId);

        for (int i = 0; i < 50; i++)
        {
            await Task.Delay(100);
            var snap = await (await client.GetAsync($"/api/analyze/async/{jobId}"))
                .Content.ReadFromJsonAsync<JsonElement>();
            string? status = snap.GetProperty("status").GetString();
            if (status == "Completed")
            {
                Assert.Equal(2, snap.GetProperty("result").GetProperty("totalObjects").GetInt32());
                return;
            }
        }
        Assert.Fail("Async config analysis did not complete in time");
    }

    [Fact]
    public async Task AnalyzeAsync_ConcurrentSubmissions_AllReachCompleted()
    {
        // 回归：AsyncAnalyzeEndpoints 之前因 Task.Run 闭包按引用捕获 tempData / tempRules，
        // 主线程紧接着把它们置为 null!，后台任务实际执行时可能读到 null →
        // CompressionDetector.Detect(null) → NRE → 作业 Failed。
        // 修复：把路径先复制到 lambda 局部变量再启动任务。
        // 这里用并发提交放大竞争窗口，单测既验证当前实现正确，也防止未来再次回归。
        const int concurrency = 8;
        var client = CreateClient();
        var jobIds = new string?[concurrency];

        for (int i = 0; i < concurrency; i++)
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent("id,name,age\n1,alice,30\n2,bob,45\n",
                System.Text.Encoding.UTF8, "text/csv"), "file", "people.csv");
            var submit = await client.PostAsync("/api/analyze/async", form);
            Assert.True(submit.IsSuccessStatusCode, await submit.Content.ReadAsStringAsync());
            jobIds[i] = (await submit.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("jobId").GetString();
            Assert.NotNull(jobIds[i]);
        }

        // 并发轮询所有作业直到终态
        var statuses = new string?[concurrency];
        var errorMessages = new string?[concurrency];
        for (int round = 0; round < 60; round++)
        {
            bool allTerminal = true;
            for (int i = 0; i < concurrency; i++)
            {
                if (statuses[i] is "Completed" or "Failed" or "Cancelled")
                    continue;
                allTerminal = false;
                var resp = await client.GetAsync($"/api/analyze/async/{jobIds[i]}");
                Assert.True(resp.IsSuccessStatusCode,
                    $"status={(int)resp.StatusCode} body={await resp.Content.ReadAsStringAsync()}");
                var snap = await resp.Content.ReadFromJsonAsync<JsonElement>();
                statuses[i] = snap.GetProperty("status").GetString();
                if (statuses[i] is "Failed" or "Cancelled"
                    && snap.TryGetProperty("error", out var errProp)
                    && errProp.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    errorMessages[i] = errProp.GetString();
                }
            }
            if (allTerminal) break;
            await Task.Delay(100);
        }

        for (int i = 0; i < concurrency; i++)
        {
            Assert.Equal("Completed", statuses[i]);
        }
    }
}
