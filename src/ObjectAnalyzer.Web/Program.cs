using ObjectAnalyzer.Web;
using ObjectAnalyzer.Web.Components;

// 自包含发布后从任意目录启动时，将内容根目录固定为可执行文件所在目录，保证 wwwroot/静态资源可用。
string appBaseDir = AppContext.BaseDirectory;
bool useAppBaseAsContentRoot = Directory.Exists(Path.Combine(appBaseDir, "wwwroot"));

var builder = WebApplication.CreateBuilder(useAppBaseAsContentRoot
    ? new WebApplicationOptions { Args = args, ContentRootPath = appBaseDir }
    : new WebApplicationOptions { Args = args });

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents(options =>
    {
        options.DetailedErrors = builder.Environment.IsDevelopment();
    });

builder.Services.AddSignalR(options =>
{
    options.MaximumReceiveMessageSize = 64 * 1024; // 64KB
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(120);
    options.KeepAliveInterval = TimeSpan.FromSeconds(15);
});

builder.Services.AddScoped<AnalysisState>();
builder.Services.AddScoped<LocalizationService>();

var app = builder.Build();

// ── 静态资产启用 ──
// 非Development环境（如 --no-launch-profile 启动、Release 直接运行）时，
// MapStaticAssets 所需的静态资产清单不会自动加载，所有资产会被解析到源 wwwroot
// 物理路径，导致 blazor.web.js / CSS 隔离包 404 或 500（页面无样式、电路无法连接）。
// 按 ASP.NET Core 官方指引在非 Development 环境显式启用静态 Web 资产
// （等价于 Development 环境下 WebApplicationBuilder 的自动行为）。
if (!builder.Environment.IsDevelopment())
{
    builder.WebHost.UseStaticWebAssets();
}

// ── 静态资产兜底中间件 ──
// MapStaticAssets 依赖构建期生成的压缩产物与指纹清单；本地增量构建被中断时
// （例如 exe 被占用导致复制失败）产物可能损坏，浏览器会拿到空 CSS/JS，
// 表现为整页无样式、电路无法连接。此中间件直接从源 wwwroot 提供本地静态文件，
// 保证任何构建状态下页面始终可用（_framework 框架资产仍由 MapStaticAssets 提供）。
// （例如 exe 被占用导致复制失败）产物可能损坏，浏览器会拿到空 CSS/JS，
// 表现为整页无样式、电路无法连接。此中间件直接从源 wwwroot 提供本地静态文件，
// 框架脚本则从 NuGet 内部资产包提供，保证任何构建状态下页面始终可用。
var sourceWwwroot = Path.Combine(builder.Environment.ContentRootPath, "wwwroot");
var staticContentTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
{
    [".css"] = "text/css",
    [".js"] = "text/javascript",
    [".mjs"] = "text/javascript",
    [".json"] = "application/json",
    [".map"] = "application/json",
    [".png"] = "image/png",
    [".svg"] = "image/svg+xml",
    [".ico"] = "image/x-icon",
    [".woff"] = "font/woff",
    [".woff2"] = "font/woff2"
};
app.Use(async (context, next) =>
{
    if (HttpMethods.IsGet(context.Request.Method))
    {
        var requestPath = context.Request.Path.Value?.TrimStart('/') ?? "";
        if (requestPath.Length > 0 &&
            !requestPath.StartsWith("_framework/", StringComparison.OrdinalIgnoreCase))
        {
            var local = Path.Combine(sourceWwwroot, requestPath.Replace('/', Path.DirectorySeparatorChar));
            if (local.StartsWith(sourceWwwroot, StringComparison.OrdinalIgnoreCase) && File.Exists(local))
            {
                context.Response.ContentType = staticContentTypes.TryGetValue(
                    Path.GetExtension(local), out var fileContentType) ? fileContentType : "application/octet-stream";
                await context.Response.SendFileAsync(local);
                return;
            }
        }
    }
    await next();
});

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// ── P6.2 检查点下载端点：仅允许从 %TEMP%/objectanalyzer-checkpoints/ 目录下载，防止路径穿越 ──
app.MapGet("/api/checkpoint/{*path}", (string path) =>
{
    // 1) 反规范化 + 边界校验
    var checkpointsRoot = Path.Combine(Path.GetTempPath(), "objectanalyzer-checkpoints");
    var fullPath = Path.GetFullPath(Path.Combine(checkpointsRoot, path));
    if (!fullPath.StartsWith(checkpointsRoot, StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest("拒绝访问：检查点路径必须在 %TEMP%/objectanalyzer-checkpoints/ 内。");
    }
    if (!System.IO.File.Exists(fullPath))
    {
        return Results.NotFound();
    }
    // 2) 限定 Content-Type 与文件名
    return Results.File(fullPath, "application/json", Path.GetFileName(fullPath));
});

app.Run();

/// <summary>供集成测试 (WebApplicationFactory) 引用的入口点。</summary>
public partial class Program { }
