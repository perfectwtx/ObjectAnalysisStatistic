using System.ComponentModel;
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

// ── 健康检查 ──
app.MapGet("/api/health", () => Results.Ok(new
{
    status = "ok",
    version = typeof(AnalysisResult).Assembly.GetName().Version?.ToString()
}))
.WithTags("系统")
.WithSummary("健康检查")
.WithDescription("返回服务状态与核心分析库版本号。");

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
})
.WithTags("规则")
.WithSummary("校验规则 JSON")
.WithDescription("请求体为规则文件 JSON（支持 // 与 /* */ 注释、字段名大小写不敏感）。返回 { valid, fields }；非法时返回 400 与错误信息。");

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
})
.WithTags("规则")
.WithSummary("返回全量规则参考模板")
.WithDescription("返回 samples/rules_full_reference.jsonc 的完整内容——覆盖全部规则配置且逐项带中文注释，可作为规则文件的起点。");

// ── 分析：multipart 上传 ──
app.MapPost("/api/analyze", async Task<IResult> (
    [FromForm] AnalyzeForm form,
    HttpRequest request,
    CancellationToken cancellationToken) =>
{
    var file = form.File;
    if (file is null || file.Length == 0)
        return Results.BadRequest(new { error = "请通过 multipart 表单上传数据文件（字段名 file）。" });

    var tempData = Path.Combine(Path.GetTempPath(), $"oa-api-{Guid.NewGuid():N}{Path.GetExtension(file.FileName)}");
    var tempRules = Path.Combine(Path.GetTempPath(), $"oa-api-{Guid.NewGuid():N}.json");
    try
    {
        await using (var fs = File.Create(tempData))
            await file.CopyToAsync(fs, cancellationToken);

        AnalysisRules? rulesObj = null;
        var rulesSource = form.RulesJson is not null ? "inline" : form.Rules is not null ? "file" : null;
        if (form.RulesJson is not null)
        {
            rulesObj = JsonSerializer.Deserialize<AnalysisRules>(form.RulesJson, AnalysisRules.RulesJsonOptions);
        }
        else if (form.Rules is not null && form.Rules.Length > 0)
        {
            await using var rfs = File.Create(tempRules);
            await form.Rules.CopyToAsync(rfs, cancellationToken);
            rulesObj = JsonSerializer.Deserialize<AnalysisRules>(
                File.ReadAllText(tempRules), AnalysisRules.RulesJsonOptions);
        }

        var format = FileParsers.DetectFormat(tempData);
        var result = AnalysisCore.AnalyzeFile(tempData, format, rulesObj, new AnalyzeRequestOptions
        {
            Flatten = form.Flatten,
            MaxValuesToShow = form.MaxValuesToShow,
            Fields = form.Fields,
            Filter = form.Filter,
            RootPath = form.RootPath,
            RecordPath = form.RecordPath,
            SampleReservoir = form.SampleReservoir,
            SampleStep = form.SampleStep,
            MaxDegreeOfParallelism = form.MaxDegreeOfParallelism
        }, cancellationToken);

        return Results.Ok(new
        {
            fileName = file.FileName,
            format,
            rulesSource,
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
.WithTags("分析")
.WithSummary("分析数据文件（multipart 上传）")
.WithDescription("上传数据文件并执行完整分析：字段统计与覆盖率、类型/语义推断、值分布、数值深度剖析（均值/标准差/分位数/直方图）、重复对象、默认值推断等。" +
    "可选规则文件或内联规则 JSON 启用枚举校验/范围检查/打码/值转换/数据集期望。响应为 camelCase JSON，字段级违规内嵌于 result.fieldStatistics。")
.DisableAntiforgery();

// ── 分析：请求体即数据文本 ──
app.MapPost("/api/analyze/raw", async Task<IResult> (
    HttpRequest request,
    [Description("数据格式：json / jsonl / csv / excel / xml / yaml")] string format,
    [Description("是否展开嵌套字段为 a.b.c 路径（默认 true）")] bool? flatten,
    [Description("每字段最多展示的唯一值数量（默认 5）")] int? maxValues,
    [Description("仅统计这些字段（逗号分隔，如 name,age）")] string? fields,
    [Description("过滤表达式，如 age >= 18 AND status in ['active']")] string? filter,
    [Description("文件根 JSONPath，如 $.data[*]（仅整体 JSON 生效）")] string? rootPath,
    [Description("记录级 JSONPath，如 $.payload（每条记录生效）")] string? recordPath,
    [Description("蓄水池随机采样最大记录数（0 = 关闭）")] int? sampleReservoir,
    [Description("等距采样步长，每 N 条取 1 条（0 = 关闭）")] int? sampleStep,
    [Description("最大并行线程数（-1 = 全部核心）")] int? maxParallel,
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
.WithTags("分析")
.WithSummary("分析请求数据文本")
.WithDescription("请求体即数据文本（JSON / JSONL / CSV / Excel / XML / YAML），通过 ?format= 指定格式；全部分析参数通过查询字符串传入。响应与 multipart 版本一致。")
.DisableAntiforgery();

app.Run();

/// <summary>供 WebApplicationFactory 测试使用。</summary>
public partial class Program;
