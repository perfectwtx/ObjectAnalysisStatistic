using System.Net;
using System.Threading.Tasks;
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
    }

    [Fact]
    public async Task AppStyles_AreServed()
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/app.css");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string content = await response.Content.ReadAsStringAsync();
        Assert.Contains("charts-section", content);
    }
}
