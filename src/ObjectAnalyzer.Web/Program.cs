using ObjectAnalyzer.Web;
using ObjectAnalyzer.Web.Components;
using ObjectAnalyzer.Web.Platform;

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

var historyDbPath = Path.Combine(appBaseDir, "data", "objectanalyzer.db");
builder.Services.AddAnalysisPlatform(historyDbPath);
builder.Services.AddHttpClient("WebhookDelivery", client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddHostedService<WebhookDeliveryWorker>();

var app = builder.Build();

// 平台历史数据库初始化
await app.Services.GetRequiredService<ObjectAnalyzer.Platform.IAnalysisHistoryStore>().InitializeAsync();
await app.Services.GetRequiredService<ObjectAnalyzer.Platform.IAlertRuleStore>().InitializeAsync();
await app.Services.GetRequiredService<ObjectAnalyzer.Platform.IWebhookDeliveryStore>().InitializeAsync();

// ── 静态资产启用 ──
if (!builder.Environment.IsDevelopment())
{
    builder.WebHost.UseStaticWebAssets();
}

// ── 静态资产兜底中间件 ──
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
        if (requestPath.Length > 0 && !requestPath.StartsWith("_framework/", StringComparison.OrdinalIgnoreCase))
        {
            var local = Path.Combine(sourceWwwroot, requestPath.Replace('/', Path.DirectorySeparatorChar));
            if (local.StartsWith(sourceWwwroot, StringComparison.OrdinalIgnoreCase) && File.Exists(local))
            {
                context.Response.ContentType = staticContentTypes.TryGetValue(Path.GetExtension(local), out var fileContentType)
                    ? fileContentType : "application/octet-stream";
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
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForErrors: true);
app.UseHttpsRedirection();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.MapGet("/api/checkpoint/{*path}", (string path) =>
{
    var checkpointsRoot = Path.Combine(Path.GetTempPath(), "objectanalyzer-checkpoints");
    var fullPath = Path.GetFullPath(Path.Combine(checkpointsRoot, path));
    if (!fullPath.StartsWith(checkpointsRoot, StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest("拒绝访问：检查点路径必须在 %TEMP%/objectanalyzer-checkpoints/ 内。");
    if (!System.IO.File.Exists(fullPath)) return Results.NotFound();
    return Results.File(fullPath, "application/json", Path.GetFileName(fullPath));
});

app.Run();

public partial class Program { }
