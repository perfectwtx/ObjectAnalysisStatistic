using System.Text.Json;
using System.Text.Json.Serialization;
using ObjectAnalyzer.Api;
using ObjectAnalyzer.Api.Endpoints;
using ObjectAnalyzer.Platform;
using Scalar.AspNetCore;

// ── ObjectAnalyzer Mini API ──
// 为 React 前端（Vite 开发服务器）提供 HTTP 分析接口。
// 端点按资源拆分在 Endpoints/ 下，这里只做启动、服务注册与路由分组挂载。
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .WithOrigins("http://localhost:5173", "http://127.0.0.1:5173", "http://localhost:3000")
    .AllowAnyHeader()
    .AllowAnyMethod()));

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.Services.AddOpenApi();

var historyDbPath = Path.Combine(AppContext.BaseDirectory, "data", "objectanalyzer.db");
builder.Services.AddAnalysisPlatform(historyDbPath);

var app = builder.Build();

await app.Services.GetRequiredService<IAnalysisHistoryStore>().InitializeAsync();
await app.Services.GetRequiredService<IQualityIssueResponsibilityStore>().InitializeAsync();
await app.Services.GetRequiredService<IQualityIssueRemediationStore>().InitializeAsync();
await app.Services.GetRequiredService<IQualityBaselineStore>().InitializeAsync();
await app.Services.GetRequiredService<IAlertRuleStore>().InitializeAsync();
await app.Services.GetRequiredService<IWebhookDeliveryStore>().InitializeAsync();

app.UseCors();

app.MapOpenApi();
app.MapScalarApiReference(options =>
{
    options.Title = "ObjectAnalyzer Mini API";
    options.Theme = ScalarTheme.Purple;
    options.DefaultHttpClient = new(ScalarTarget.CSharp, ScalarClient.HttpClient);
});

var api = app.MapGroup("/api");
api.MapHealthApi();
api.MapRulesApi();
api.MapAnalyzeApi();
api.MapPreflightApi();
api.MapAsyncAnalyzeApi();
api.MapQualityBaselineApi();
api.MapDataLineageEndpoints();
api.MapImpactAnalysisEndpoints();
api.MapAnalysisHistoryApi();
api.MapQualityApi();

app.Run();

/// <summary>供 WebApplicationFactory 测试使用。</summary>
public partial class Program;