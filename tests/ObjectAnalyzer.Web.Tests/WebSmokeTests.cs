using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ObjectAnalyzer.Web.Tests;

/// <summary>Web 冒烟测试：验证应用可启动、首页可访问、静态资源与互操作脚本就绪。</summary>
public class WebSmokeTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public WebSmokeTests(WebApplicationFactory<Program> factory)
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
        // P6 子区段样式：details/summary 折叠
        Assert.Contains(".option-group", content);
    }

    /// <summary>
    /// P6 Web UI 同步冒烟：首页初始渲染时即展示 P6 配置（进度条、内存预算、断点续跑、基线合并、检查点）。
    /// 验证 P6 折叠区与关键控件同时出现在初始 HTML（SSR）输出。
    /// 因 Razor 对中文字符做 HTML 实体编码（&amp;#x...），断言以可移植的 ASCII 标签为主，
    /// 并对中文/破折号做编码/非编码两种形式的 OR 校验。
    /// </summary>
    [Fact]
    public async Task HomePage_RendersP6ConfigSection()
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string content = await response.Content.ReadAsStringAsync();

        // DOM 节点选择器
        Assert.Contains("opt-group-title", content);
        Assert.Contains("option-group", content);
        // 关键控件 ID/Name/类型（type=number / type=checkbox 与 P6 选项挂钩）
        Assert.Contains("\"opt-group-hint\"", content);
        // 检查 P6 关键 label 文本：HTML 实体或直接 UTF-8（取决于 Razor 编码设置）
        Assert.True(
            content.Contains("P6 — 增量 / 内存 / 进度")
            || content.Contains("P6 &#x2014; &#x589E;&#x91CF; / &#x5185;&#x5B58; / &#x8FDB;&#x5EA6;"),
            "P6 折叠区标题未在 HTML 中出现（应使用中文本地化）。");
        // 关键 label 文本（任一编码形式）
        Assert.True(
            content.Contains("显示进度条 / ETA") || content.Contains("&#x663E;&#x793A;&#x8FDB;&#x5EA6;&#x6761; / ETA"),
            "OptShowProgress 标签未出现。");
        Assert.True(
            content.Contains("内存上限 (MB)") || content.Contains("&#x5185;&#x5B58;&#x4E0A;&#x9650; (MB)"),
            "OptMaxMemory 标签未出现。");
        Assert.True(
            content.Contains("断点续跑 (.snapshot)") || content.Contains("&#x65AD;&#x70B9;&#x7EED;&#x8DD1; (.snapshot)"),
            "OptResume 标签未出现。");
        Assert.True(
            content.Contains("基线合并 (--baseline)") || content.Contains("&#x57FA;&#x7EBF;&#x5408;&#x5E76; (--baseline)"),
            "OptBaseline 标签未出现。");
        Assert.True(
            content.Contains("检查点输出 (--checkpoint)") || content.Contains("&#x68C0;&#x67E5;&#x70B9;&#x8F93;&#x51FA; (--checkpoint)"),
            "OptCheckpoint 标签未出现。");
    }

    /// <summary>
    /// P6.2 检查点下载端点：路径穿越保护。绝对不能让浏览器读取 %TEMP% 之外的任何文件。
    /// 验证 <c>"../"</c> 形式被服务端拒绝。
    /// </summary>
    [Theory]
    [InlineData("..%2F..%2Fboot.ini")]      // URL 编码的 ..
    [InlineData("..%5C..%5Cboot.ini")]      // URL 编码的 ..\
    [InlineData("..%2f..%2fboot.ini")]      // 小写 url 编码
    public async Task CheckpointEndpoint_RejectsPathTraversal(string encodedPath)
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync($"/api/checkpoint/{encodedPath}");

        // 反规范化后的路径必须被识别为越界 —— 服务端应返回 4xx 而非 200
        Assert.True(
            response.StatusCode == HttpStatusCode.BadRequest
            || response.StatusCode == HttpStatusCode.NotFound,
            $"路径 {encodedPath} 应被拒绝，但服务端返回 {(int)response.StatusCode}");
    }

    /// <summary>
    /// P6.2 检查点下载端点：合法路径返回 200 + 正确的 Content-Type。
    /// 在 %TEMP%/objectanalyzer-checkpoints/ 写入一个 fixture 文件后请求下载，验证文件可被服务出去。
    /// </summary>
    [Fact]
    public async Task CheckpointEndpoint_ServesValidSnapshot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "objectanalyzer-checkpoints");
        Directory.CreateDirectory(dir);
        var snapshotPath = Path.Combine(dir, $"web-tests-{Guid.NewGuid():N}.snapshot");
        try
        {
            // 写一个最小的 AnalyzerSnapshot 兼容 JSON
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
            try { File.Delete(snapshotPath); } catch { /* 忽略清理异常 */ }
        }
    }
}
