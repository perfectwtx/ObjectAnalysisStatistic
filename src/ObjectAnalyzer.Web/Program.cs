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
