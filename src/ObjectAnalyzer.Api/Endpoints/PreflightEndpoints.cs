using System.ComponentModel;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using ObjectAnalyzer.Analysis;
using ObjectAnalyzer.Parsing;

namespace ObjectAnalyzer.Api;

/// <summary>
/// 预检端点：POST /api/preflight（multipart 上传 + 内联规则）。
/// 复用 Core 的 PreflightChecker，与 /api/analyze 解析路径完全一致：
/// 调用方据此判断「规则与数据是否匹配」，决定是否继续发起分析请求。
/// </summary>
public static class PreflightEndpoints
{
    public static IEndpointRouteBuilder MapPreflightApi(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/preflight",
            async Task<Results<Ok<PreflightResponse>, BadRequest<ErrorResponse>>> (
            [FromForm] PreflightForm form,
            HttpRequest request,
            CancellationToken cancellationToken) =>
        {
            var file = form.File;
            if (file is null || file.Length == 0)
                return TypedResults.BadRequest(new ErrorResponse("请通过 multipart 表单上传数据文件（字段名 file）。"));

            var tempData = Path.Combine(Path.GetTempPath(), $"oa-preflight-{Guid.NewGuid():N}{Path.GetExtension(file.FileName)}");
            var tempRules = Path.Combine(Path.GetTempPath(), $"oa-preflight-{Guid.NewGuid():N}.json");
            try
            {
                await using (var fs = File.Create(tempData))
                    await file.CopyToAsync(fs, cancellationToken);

                string? rulesText = form.RulesJson;
                if (rulesText is null && form.Rules is not null && form.Rules.Length > 0)
                {
                    await using (var rfs = File.Create(tempRules))
                        await form.Rules.CopyToAsync(rfs, cancellationToken);
                    rulesText = await File.ReadAllTextAsync(tempRules, cancellationToken);
                }

                AnalysisRules? rulesObj = null;
                if (!string.IsNullOrWhiteSpace(rulesText))
                {
                    var (rules, err) = AnalysisCore.ParseRules(rulesText, form.AllowUnknownRules is true);
                    if (err is not null) return TypedResults.BadRequest(err);
                    rulesObj = rules;
                }

                // 解析选项只用 preflight 关心的部分（其余由 ParseOptions 默认填充）
                var options = new AnalysisOptions
                {
                    Input = { InputPath = tempData, RecordPath = form.RecordPath },
                    Parse =
                    {
                        Flatten = form.Flatten ?? true,
                        CsvInferNumbers = form.CsvInferNumbers ?? true
                    }
                };

                var result = AnalysisCore.Preflight(tempData, rulesObj, options);
                return TypedResults.Ok(new PreflightResponse(result));
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
        .WithTags("预检")
        .WithSummary("预检：对比规则字段与数据样本字段")
        .WithDescription("""
        上传数据文件与规则文件（或内联规则 JSON），调用 Core 的 PreflightChecker：
        - 解析前 N 条记录
        - 大小写不敏感 + 父路径匹配规则字段
        - 返回 hasWarning / ruleFields / matchedRuleFields / sampleFields

        命中即返回 200 + 结构化结果；规则无法反序列化、未知键等仍走 400。客户端据此决定是否继续调用 /api/analyze。
        """)
        .DisableAntiforgery();

        // 强类型 JSON body 预检（数据文本内嵌，与 /analyze/config 字段一致）
        routes.MapPost("/preflight/config",
            async Task<Results<Ok<PreflightResponse>, BadRequest<ErrorResponse>>> (
            [FromBody] PreflightConfigRequest config,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(config.DataText))
                return TypedResults.BadRequest(new ErrorResponse("缺少数据文本：请提供 dataText。"));

            var tempData = Path.Combine(Path.GetTempPath(), $"oa-preflight-{Guid.NewGuid():N}.txt");
            try
            {
                await File.WriteAllTextAsync(tempData, config.DataText, cancellationToken);

                AnalysisRules? rulesObj = null;
                if (!string.IsNullOrWhiteSpace(config.RulesJson))
                {
                    var (rules, err) = AnalysisCore.ParseRules(config.RulesJson, config.AllowUnknownRules is true);
                    if (err is not null) return TypedResults.BadRequest(err);
                    rulesObj = rules;
                }

                var options = new AnalysisOptions
                {
                    Input = { InputPath = tempData, RecordPath = config.RecordPath },
                    Parse =
                    {
                        Flatten = config.Flatten ?? true,
                        CsvInferNumbers = config.CsvInferNumbers ?? true
                    }
                };

                var result = AnalysisCore.Preflight(tempData, rulesObj, options);
                return TypedResults.Ok(new PreflightResponse(result));
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
        .WithTags("预检")
        .WithSummary("预检：强类型 JSON 请求体（数据文本内嵌）")
        .WithDescription("与 /api/preflight 的 multipart 版本等价，但请求体是纯 JSON；dataText 内嵌数据，rulesJson 内联规则。")
        .DisableAntiforgery();

        return routes;
    }
}

/// <summary>multipart 预检表单（仅取 preflight 关心的字段，避免暴露完整 AnalyzeForm）。</summary>
public sealed class PreflightForm
{
    /// <summary>数据文件（必填）。</summary>
    [Description("数据文件（JSON/JSONL/CSV/Excel/XML/YAML）")]
    public IFormFile? File { get; init; }

    /// <summary>规则文件（与 rulesJson 二选一）。</summary>
    [Description("规则文件（.json）")]
    public IFormFile? Rules { get; init; }

    /// <summary>内联规则 JSON（优先于 Rules）。</summary>
    [Description("内联规则 JSON")]
    public string? RulesJson { get; init; }

    /// <summary>是否跳过未知键检查（默认 false）。</summary>
    [Description("跳过规则未知键检查（默认 false）")]
    public bool? AllowUnknownRules { get; init; }

    /// <summary>JSON 展平（默认 true）。</summary>
    [Description("JSON 嵌套字段展平（默认 true）")]
    public bool? Flatten { get; init; }

    /// <summary>CSV 数字列推断（默认 true）。</summary>
    [Description("CSV 数字列推断为数值类型（默认 true）")]
    public bool? CsvInferNumbers { get; init; }

    /// <summary>记录级 JSONPath。</summary>
    [Description("记录级 JSONPath（如 $.payload）")]
    public string? RecordPath { get; init; }
}

/// <summary>强类型预检请求体（数据文本 + 规则 JSON 内嵌）。</summary>
public sealed class PreflightConfigRequest
{
    /// <summary>数据文本（必填）。</summary>
    [Description("数据文本（JSON/JSONL/CSV/XML/YAML）")]
    public string DataText { get; init; } = "";

    /// <summary>内联规则 JSON。</summary>
    [Description("内联规则 JSON")]
    public string? RulesJson { get; init; }

    /// <summary>是否跳过未知键检查（默认 false）。</summary>
    [Description("跳过规则未知键检查（默认 false）")]
    public bool? AllowUnknownRules { get; init; }

    /// <summary>JSON 展平（默认 true）。</summary>
    [Description("JSON 嵌套字段展平（默认 true）")]
    public bool? Flatten { get; init; }

    /// <summary>CSV 数字列推断（默认 true）。</summary>
    [Description("CSV 数字列推断为数值类型（默认 true）")]
    public bool? CsvInferNumbers { get; init; }

    /// <summary>记录级 JSONPath。</summary>
    [Description("记录级 JSONPath")]
    public string? RecordPath { get; init; }
}
