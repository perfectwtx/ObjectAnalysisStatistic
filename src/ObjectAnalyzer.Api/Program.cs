using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using ObjectAnalyzer.Analysis;
using ObjectAnalyzer.Analysis.Rules;
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
app.MapGet("/api/health", () =>
{
    var version = typeof(AnalysisResult).Assembly.GetName().Version?.ToString();
    return TypedResults.Ok(new HealthResponse("ok", version));
})
.WithTags("系统")
.WithSummary("健康检查")
.WithDescription("返回服务状态与核心分析库版本号。");

// ── 规则校验 ──
// 除反序列化本身的错误（结构写错、类型写错）外，额外列出「写了但后端不认识」的键：
// STJ 默认静默忽略未知成员，规则名拼错时用户得不到任何反馈，规则看似生效实则完全没生效。
// 所以未知键默认直接算校验不通过（valid=false），调用方据此阻断分析；
// 确实要放行时追加 ?strict=false，退回只提示不拦截。
app.MapPost("/api/rules/validate",
    async Task<Results<Ok<RulesValidationResponse>, BadRequest<RulesValidationResponse>>> (HttpRequest request) =>
{
    using var reader = new StreamReader(request.Body);
    var json = await reader.ReadToEndAsync();

    var unknownRaw = RulesLint.Scan(json);
    var unknowns = unknownRaw.Select(k => new RulesLintEntry(k.Path, k.Name, k.Suggestion)).ToList();
    var strict = request.Query["strict"].ToString() is not ("false" or "0");

    try
    {
        var rules = JsonSerializer.Deserialize<AnalysisRules>(json, AnalysisRules.RulesJsonOptions);
        // 未知键与结构错误一样走 200 + valid=false：返回 4xx 会让调用方丢掉 unknowns 明细，
        // UI 就没法逐条提示"是不是想写 xxx"了
        var unknownError = strict && unknownRaw.Count > 0 ? RulesLint.Describe(unknownRaw) : null;
        return TypedResults.Ok(new RulesValidationResponse(
            Valid: rules != null && unknownError is null,
            Fields: rules?.Fields.Count ?? 0,
            Unknowns: unknowns,
            Error: unknownError));
    }
    catch (Exception ex)
    {
        return TypedResults.BadRequest(new RulesValidationResponse(
            Valid: false, Fields: 0, Unknowns: unknowns, Error: ex.Message));
    }
})
.WithTags("规则")
.WithSummary("校验规则 JSON")
.WithDescription("""
请求体为规则文件 JSON（支持 // 与 /* */ 注释、字段名大小写不敏感）。
返回 { valid, fields, unknowns, error? }：unknowns 是后端不认识的键（拼写错误或已废弃），
每项带 path / name / suggestion（按编辑距离猜的正确键名）。
默认未知键即判校验不通过（valid=false，error 为中文说明）；追加 ?strict=false 时只提示不拦截。
""");

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
app.MapPost("/api/analyze",
    async Task<Results<Ok<AnalyzeResponse>, BadRequest<ErrorResponse>>> (
    [FromForm] AnalyzeForm form,
    HttpRequest request,
    CancellationToken cancellationToken) =>
{
    var file = form.File;
    if (file is null || file.Length == 0)
        return TypedResults.BadRequest(new ErrorResponse("请通过 multipart 表单上传数据文件（字段名 file）。"));

    var tempData = Path.Combine(Path.GetTempPath(), $"oa-api-{Guid.NewGuid():N}{Path.GetExtension(file.FileName)}");
    var tempRules = Path.Combine(Path.GetTempPath(), $"oa-api-{Guid.NewGuid():N}.json");
    try
    {
        await using (var fs = File.Create(tempData))
            await file.CopyToAsync(fs, cancellationToken);

        AnalysisRules? rulesObj = null;
        var rulesSource = form.RulesJson is not null ? "inline" : form.Rules is not null ? "file" : null;

        // 内联规则优先；规则文件先落盘再读文本——反序列化前要拿原文做一次未知键扫描
        string? rulesText = form.RulesJson;
        if (rulesText is null && form.Rules is not null && form.Rules.Length > 0)
        {
            await using (var rfs = File.Create(tempRules))
                await form.Rules.CopyToAsync(rfs, cancellationToken);
            rulesText = await File.ReadAllTextAsync(tempRules, cancellationToken);
        }

        if (rulesText is not null)
        {
            // 与 /api/rules/validate 同源的检查：拼错的规则名在这里同样被拦下，
            // 而不是"通过了校验却静默不生效"。确认可忽略时传 allowUnknownRules=true。
            if (form.AllowUnknownRules is not true)
            {
                var unknownRaw = RulesLint.Scan(rulesText);
                if (unknownRaw.Count > 0)
                    return TypedResults.BadRequest(ApiErrors.UnknownRules(unknownRaw));
            }

            rulesObj = JsonSerializer.Deserialize<AnalysisRules>(rulesText, AnalysisRules.RulesJsonOptions);
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
            MaxDegreeOfParallelism = form.MaxDegreeOfParallelism,
            CsvInferNumbers = form.CsvInferNumbers,
            EnableCorrelation = form.EnableCorrelation,
            EnableStringPatterns = form.EnableStringPatterns,
            EnableUnicodeHygiene = form.EnableUnicodeHygiene,
            EnableTimeSeries = form.EnableTimeSeries,
            EnableDistributionSnapshot = form.EnableDistributionSnapshot
        }, cancellationToken);

        return TypedResults.Ok(new AnalyzeResponse(
            FileName: file.FileName,
            Format: format,
            RulesSource: rulesSource,
            Result: result));
    }
    catch (Exception ex)
    {
        return TypedResults.BadRequest(ApiErrors.FromException(ex));
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
    "可选规则文件或内联规则 JSON 启用枚举校验/范围检查/打码/值转换/数据集期望。" +
    "规则里若出现后端不认识的键（拼错/已废弃），默认直接返回 400 并列出 unknowns，" +
    "避免规则看似生效实则被静默忽略；确认无需处理时传 allowUnknownRules=true。" +
    "响应为 camelCase JSON，字段级违规内嵌于 result.fieldStatistics。")
.DisableAntiforgery();

// ── 分析：请求体即数据文本 ──
app.MapPost("/api/analyze/raw",
    async Task<Results<Ok<AnalyzeRawResponse>, BadRequest<ErrorResponse>>> (
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
    [Description("CSV 数字列推断为数值类型（默认 true，false 则按字符串统计）")] bool? csvInferNumbers,
    [Description("启用字段相关性分析")] bool? enableCorrelation,
    [Description("启用字符串模式归纳")] bool? enableStringPatterns,
    [Description("启用 Unicode 卫生检查")] bool? enableUnicodeHygiene,
    [Description("启用时间序列分析")] bool? enableTimeSeries,
    [Description("启用分布快照导出")] bool? enableDistributionSnapshot,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(format))
        return TypedResults.BadRequest(new ErrorResponse("请通过 ?format= 指定数据格式（json / jsonl / csv…）。"));

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
                MaxDegreeOfParallelism = maxParallel,
                CsvInferNumbers = csvInferNumbers,
                EnableCorrelation = enableCorrelation,
                EnableStringPatterns = enableStringPatterns,
                EnableUnicodeHygiene = enableUnicodeHygiene,
                EnableTimeSeries = enableTimeSeries,
                EnableDistributionSnapshot = enableDistributionSnapshot
            }, cancellationToken);

        return TypedResults.Ok(new AnalyzeRawResponse(format.Trim().ToLowerInvariant(), result));
    }
    catch (Exception ex)
    {
        return TypedResults.BadRequest(ApiErrors.FromException(ex));
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
