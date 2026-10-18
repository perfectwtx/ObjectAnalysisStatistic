using System.Net;

namespace ObjectAnalyzer.Web.Tests;

/// <summary>Web 冒烟测试：验证应用可启动、首页可访问、静态资源与核心质量分析页面可访问。</summary>
public class WebSmokeTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public WebSmokeTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task HomePage_ReturnsOk()
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string content = await response.Content.ReadAsStringAsync();
        Assert.Contains("ObjectAnalyzer", content);
    }

    [Fact]
    public async Task NotFoundPage_ReturnsNotFound()
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/no-such-page");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task InteropScript_IsServed()
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/interop.js");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string content = await response.Content.ReadAsStringAsync();
        Assert.Contains("downloadTextFile", content);
        Assert.Contains("setTheme", content);
        Assert.Contains("isDarkTheme", content);
    }

    [Fact]
    public async Task AppStyles_AreServed()
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/app.css");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string content = await response.Content.ReadAsStringAsync();
        Assert.Contains("charts-section", content);
        Assert.Contains("data-bs-theme=\"dark\"", content);
        Assert.Contains(".theme-toggle", content);
        Assert.Contains(".option-group", content);
    }

    [Fact]
    public async Task HomePage_RendersP6ConfigSection()
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string content = await response.Content.ReadAsStringAsync();

        Assert.Contains("opt-group-title", content);
        Assert.Contains("option-group", content);
        Assert.Contains("\"opt-group-hint\"", content);
        Assert.True(
            content.Contains("增量 / 内存 / 进度")
            || content.Contains("&#x589E;&#x91CF; / &#x5185;&#x5B58; / &#x8FDB;&#x5EA6;"),
            "P6 折叠区标题未在 HTML 中出现（应使用中文本地化）。");
        Assert.True(
            content.Contains("显示进度条 / ETA") || content.Contains("&#x663E;&#x793A;&#x8FDB;&#x5EA6;&#x6761; / ETA"),
            "OptShowProgress 标签未出现。");
        Assert.True(
            content.Contains("内存上限 (MB)") || content.Contains("&#x5185;&#x5B58;&#x4E0A;&#x9650; (MB)"),
            "OptMaxMemory 标签未出现。");
        Assert.True(
            content.Contains("导入批次状态 (.snapshot)") || content.Contains(System.Text.Encodings.Web.HtmlEncoder.Default.Encode("导入批次状态 (.snapshot)")),
            "OptResume 标签未出现。");
        Assert.True(
            content.Contains("合并状态快照 (--baseline)") || content.Contains(System.Text.Encodings.Web.HtmlEncoder.Default.Encode("合并状态快照 (--baseline)")),
            "OptBaseline 标签未出现。");
        Assert.True(
            content.Contains("检查点输出 (--checkpoint)") || content.Contains("&#x68C0;&#x67E5;&#x70B9;&#x8F93;&#x51FA; (--checkpoint)"),
            "OptCheckpoint 标签未出现。");
    }

    [Theory]
    [InlineData("..%2F..%2Fboot.ini")]
    [InlineData("..%5C..%5Cboot.ini")]
    [InlineData("..%2f..%2fboot.ini")]
    public async Task CheckpointEndpoint_RejectsPathTraversal(string encodedPath)
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync($"/api/checkpoint/{encodedPath}");

        Assert.True(
            response.StatusCode == HttpStatusCode.BadRequest
            || response.StatusCode == HttpStatusCode.NotFound,
            $"路径 {encodedPath} 应被拒绝，但服务端返回 {(int)response.StatusCode}");
    }

    [Fact]
    public async Task CheckpointEndpoint_ServesValidSnapshot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "objectanalyzer-checkpoints");
        Directory.CreateDirectory(dir);
        var snapshotPath = Path.Combine(dir, $"web-tests-{Guid.NewGuid():N}.snapshot");
        try
        {
            var minimal = """
                {
                  "schema": "object-analyzer.snapshot/v1",
                  "createdAt": "2026-01-01T00:00:00Z",
                  "sourcePath": "test.json",
                  "sourceFormat": "jsonl",
                  "sourcePosition": 0,
                  "positionKind": "line",
                  "sourceFingerprint": "",
                  "result": {
                    "totalObjects": 0,
                    "totalUniqueObjects": 0,
                    "totalDuplicateGroups": 0,
                    "duplicateObjects": [],
                    "fieldStatistics": [],
                    "qualityViolations": []
                  }
                }
                """;
            await File.WriteAllTextAsync(snapshotPath, minimal);

            using var client = _factory.CreateClient();
            using var response = await client.GetAsync(
                $"/api/checkpoint/{Uri.EscapeDataString(Path.GetFileName(snapshotPath))}");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            string body = await response.Content.ReadAsStringAsync();
            Assert.Contains("object-analyzer.snapshot/v1", body);
        }
        finally
        {
            try { File.Delete(snapshotPath); } catch { }
        }
    }

    [Theory]
    [InlineData("/quality", "数据质量总览") ]
    [InlineData("/quality/trend", "质量趋势")]
    [InlineData("/quality/fields", "字段质量")]
    [InlineData("/quality/recommendations", "质量修复建议")]
    [InlineData("/quality/regression", "质量回归")]
    [InlineData("/quality/schema-impact", "Schema 影响")]
    [InlineData("/quality/impact-analysis", "影响分析")]
    [InlineData("/quality/data-lineage", "数据血缘")]
    [InlineData("/quality/relationships", "字段关系分析")]
    [InlineData("/quality/object-relationships", "对象关系")]
    [InlineData("/quality/baselines", "质量基线")]
    [InlineData("/quality/webhooks", "Webhook")]
    [InlineData("/issues", "Issue Center")]
    [InlineData("/issues/responsibility", "问题责任")]
    [InlineData("/jobs", "分析作业中心")]
    public async Task CoreWebPages_ReturnOk(string path, string title)
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string content = await response.Content.ReadAsStringAsync();
        Assert.Contains(title, content);
    }
}
