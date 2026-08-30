using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using ObjectAnalyzer.Analysis;
using ObjectAnalyzer.Api;
using ObjectAnalyzer.Parsing;
using Scalar.AspNetCore;

// ── ObjectAnalyzer Mini API ──
// 为 React 前端（Vite 开发服务器）提供 HTTP 分析接口：
//   POST /api/analyze        multipart 上传数据文件（+ 可选规则文件/内联规则 JSON）→ 分析结果
//   POST /api/analyze/raw    请求体即数据文本（JSON / JSONL / CSV…，按 ?format= 指定）→ 分析结果
//   POST /api/rules/validate 校验规则 JSON 是否合法
//   GET  /api/rules/reference 返回全量规则参考模板（samples/rules_full_reference.jsonc）
//   GET  /api/health         健康检查
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

// OpenAPI 文档 + Scalar 交互式界面（/scalar）
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

// ── 健康检查 ──
app.MapGet("/api/health", () => Results.Ok(new
{
    status = "ok",
    version = typeof(AnalysisResult).Assembly.GetName().Version?.ToString()
}));

// ── 规则校验 ──
app.MapPost("/api/rules/validate", async (HttpRequest request) =>
{
    try
    {
        using var reader = new StreamReader(request.Body);
        var json = await reader.ReadToEndAsync();
        var rules = JsonSerializer.Deserialize<AnalysisRules>(json, AnalysisRules.RulesJsonOptions);
        return Results.Ok(new { valid = rules != null, fields = rules?.Fields.Count ?? 0 });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { valid = false, error = ex.Message });
    }
});

// ── 规则参考模板 ──
app.MapGet("/api/rules/reference", () =>
{
    // 依次尝试：ContentRoot/samples → 仓库根 samples（开发布局）
    string[] candidates =
    [
        Path.Combine(app.Environment.ContentRootPath, "samples", "rules_full_reference.jsonc"),
        Path.Combine(app.Environment.ContentRootPath, "..", "..", "samples", "rules_full_reference.jsonc")
    ];
    var path = candidates.FirstOrDefault(File.Exists);
    return path is null
        ? Results.NotFound(new { error = "参考模板文件不存在（samples/rules_full_reference.jsonc）" })
        : Results.Text(File.ReadAllText(path), "application/json");
});

// ── 分析：multipart 上传 ──
app.MapPost("/api/analyze", async Task<IResult> (
    [FromForm] IFormFile file,
    [FromForm] IFormFile? rules,
    [FromForm] string? rulesJson,
    [FromForm] bool? flatten,
    [FromForm] int? maxValues,
    [FromForm] string? fields,
    [FromForm] string? filter,
    [FromForm] string? rootPath,
    [FromForm] string? recordPath,
    [FromForm] int? sampleReservoir,
    [FromForm] int? sampleStep,
    [FromForm] int? maxParallel,
    HttpRequest request,
    CancellationToken cancellationToken) =>
{
    if (file is null || file.Length == 0)
        return Results.BadRequest(new { error = "请通过 multipart 表单上传数据文件（字段名 file）。" });

    var tempData = Path.Combine(Path.GetTempPath(), $"oa-api-{Guid.NewGuid():N}{Path.GetExtension(file.FileName)}");
    var tempRules = Path.Combine(Path.GetTempPath(), $"oa-api-{Guid.NewGuid():N}.json");
    try
    {
        await using (var fs = File.Create(tempData))
            await file.CopyToAsync(fs, cancellationToken);

        AnalysisRules? rulesObj = null;
        var rulesSource = rulesJson is not null ? "inline" : rules is not null ? "file" : null;
        if (rulesJson is not null)
        {
            rulesObj = JsonSerializer.Deserialize<AnalysisRules>(rulesJson, AnalysisRules.RulesJsonOptions);
        }
        else if (rules is not null && rules.Length > 0)
        {
            await using var rfs = File.Create(tempRules);
            await rules.CopyToAsync(rfs, cancellationToken);
            rulesObj = JsonSerializer.Deserialize<AnalysisRules>(
                File.ReadAllText(tempRules), AnalysisRules.RulesJsonOptions);
        }

        var format = FileParsers.DetectFormat(tempData);
        var result = AnalysisCore.AnalyzeFile(tempData, format, rulesObj, new AnalyzeRequestOptions
        {
            Flatten = flatten,
            MaxValuesToShow = maxValues,
            Fields = fields,
            Filter = filter,
            RootPath = rootPath,
            RecordPath = recordPath,
            SampleReservoir = sampleReservoir,
            SampleStep = sampleStep,
            MaxDegreeOfParallelism = maxParallel
        }, cancellationToken);

        return Results.Ok(new
        {
            fileName = file.FileName,
            format,
            result
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    finally
    {
        try { if (File.Exists(tempData)) File.Delete(tempData); } catch { }
        try { if (File.Exists(tempRules)) File.Delete(tempRules); } catch { }
    }
})
.DisableAntiforgery();

// ── 分析：请求体即数据文本 ──
app.MapPost("/api/analyze/raw", async Task<IResult> (
    HttpRequest request,
    string format,
    [FromQuery] bool? flatten,
    [FromQuery] int? maxValues,
    [FromQuery] string? fields,
    [FromQuery] string? filter,
    [FromQuery] string? rootPath,
    [FromQuery] string? recordPath,
    [FromQuery] int? sampleReservoir,
    [FromQuery] int? sampleStep,
    [FromQuery] int? maxParallel,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(format))
        return Results.BadRequest(new { error = "请通过 ?format= 指定数据格式（json / jsonl / csv…）。" });

    var tempData = Path.Combine(Path.GetTempPath(), $"oa-api-{Guid.NewGuid():N}.{format.Trim('.').ToLowerInvariant()}");
    try
    {
        await using (var fs = File.Create(tempData))
            await request.Body.CopyToAsync(fs, cancellationToken);

        var result = AnalysisCore.AnalyzeFile(tempData, format.Trim().ToLowerInvariant(), null,
            new AnalyzeRequestOptions
            {
                Flatten = flatten,
                MaxValuesToShow = maxValues,
                Fields = fields,
                Filter = filter,
                RootPath = rootPath,
                RecordPath = recordPath,
                SampleReservoir = sampleReservoir,
                SampleStep = sampleStep,
                MaxDegreeOfParallelism = maxParallel
            }, cancellationToken);

        return Results.Ok(new { format = format.Trim().ToLowerInvariant(), result });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    finally
    {
        try { if (File.Exists(tempData)) File.Delete(tempData); } catch { }
    }
})
.DisableAntiforgery();

app.Run();

/// <summary>供 WebApplicationFactory 测试使用。</summary>
public partial class Program;
