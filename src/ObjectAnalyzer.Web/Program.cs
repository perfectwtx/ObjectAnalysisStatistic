using ObjectAnalyzer.Platform;
using ObjectAnalyzer.Web;
using ObjectAnalyzer.Web.Components;
using ObjectAnalyzer.Web.Platform;

// 自包含发布后从任意目录启动时，将内容根目录固定为可执行文件所在目录，保证 wwwroot/静态资源可用。
string appBaseDir = AppContext.BaseDirectory;
bool useAppBaseAsContentRoot = Directory.Exists(Path.Combine(appBaseDir, "wwwroot"));

var builder = WebApplication.CreateBuilder(useAppBaseAsContentRoot
    ? new WebApplicationOptions { Args = args, ContentRootPath = appBaseDir }
    : new WebApplicationOptions { Args = args });

// 静态 Web 资产需要在 Build 之前配置 WebHost。
// WebApplication 构建完成后不再提供 WebHost 配置入口。
if (!builder.Environment.IsDevelopment())
{
    builder.WebHost.UseStaticWebAssets();
}

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
builder.Services.AddScoped<WebAnalysisExecutionService>();
builder.Services.AddHttpClient("WebhookDelivery", client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
});

// 集成测试不启动后台投递线程，避免测试宿主与真实 Webhook Outbox worker 相互干扰。
if (!builder.Environment.IsEnvironment("Testing"))
{
    builder.Services.AddHostedService<ObjectAnalyzer.Web.Platform.WebhookDeliveryWorker>();
}

var app = builder.Build();

// 平台历史数据库初始化
await app.Services.GetRequiredService<IAnalysisHistoryStore>().InitializeAsync();
await using (var startupScope = app.Services.CreateAsyncScope())
{
    await startupScope.ServiceProvider.GetRequiredService<AnalysisJobService>().RecoverOrphanedJobsAsync();
}
await app.Services.GetRequiredService<IQualityIssueResponsibilityStore>().InitializeAsync();
await app.Services.GetRequiredService<IQualityIssueRemediationStore>().InitializeAsync();
await app.Services.GetRequiredService<IQualityBaselineStore>().InitializeAsync();
await app.Services.GetRequiredService<IAlertRuleStore>().InitializeAsync();
await app.Services.GetRequiredService<IWebhookDeliveryStore>().InitializeAsync();

// 使用框架文件提供器限定 wwwroot 边界，并处理路径规范化。
app.UseStaticFiles();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.MapGet("/api/checkpoint/{*path}", (string path) =>
{
    var checkpointsRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "objectanalyzer-checkpoints")) + Path.DirectorySeparatorChar;
    string fullPath;
    try
    {
        fullPath = Path.GetFullPath(Path.Combine(checkpointsRoot, path.Replace('\\', Path.DirectorySeparatorChar)));
    }
    catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
    {
        return Results.BadRequest("无效的检查点路径。");
    }
    if (!fullPath.StartsWith(checkpointsRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        return Results.BadRequest("拒绝访问：检查点路径必须在 %TEMP%/objectanalyzer-checkpoints/ 内。");
    if (!System.IO.File.Exists(fullPath)) return Results.NotFound();
    return Results.File(fullPath, "application/json", Path.GetFileName(fullPath));
});

app.Run();

public partial class Program { }
