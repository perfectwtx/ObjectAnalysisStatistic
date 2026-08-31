using System.Text.Json;
using System.Text.Json.Serialization;
using ObjectAnalyzer.Api;
using Scalar.AspNetCore;

// ── ObjectAnalyzer Mini API ──
// 为 React 前端（Vite 开发服务器）提供 HTTP 分析接口。
// 端点按资源拆分在 Endpoints/ 下，这里只做启动、服务注册与路由分组挂载：
//   Endpoints/HealthEndpoints.cs   GET  /api/health
//   Endpoints/RulesEndpoints.cs    POST /api/rules/validate、GET /api/rules/reference
//   Endpoints/AnalyzeEndpoints.cs  POST /api/analyze、/api/analyze/config、/api/analyze/raw
var builder = WebApplication.CreateBuilder(args);

// React 前端（Vite 开发服务器）跨域访问
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .WithOrigins("http://localhost:5173", "http://127.0.0.1:5173", "http://localhost:3000")
    .AllowAnyHeader()
    .AllowAnyMethod()));

// 响应统一 camelCase、省略 null
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

// OpenAPI 文档 + Scalar 交互式界面（/scalar）。
// GenerateDocumentationFile 开启后，类型/属性的 XML 注释会自动进入 OpenAPI 描述。
builder.Services.AddOpenApi();

var app = builder.Build();
app.UseCors();

app.MapOpenApi();           // GET /openapi/v1.json
app.MapScalarApiReference(options =>
{
    options.Title = "ObjectAnalyzer Mini API";
    options.Theme = ScalarTheme.Purple;
    options.DefaultHttpClient = new(ScalarTarget.CSharp, ScalarClient.HttpClient);
});                         // GET /scalar

// 按资源挂载端点组（统一前缀 /api）
var api = app.MapGroup("/api");
api.MapHealthApi();
api.MapRulesApi();
api.MapAnalyzeApi();

app.Run();

/// <summary>供 WebApplicationFactory 测试使用。</summary>
public partial class Program;
