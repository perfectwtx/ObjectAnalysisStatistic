using System.ComponentModel;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using ObjectAnalyzer.Analysis;
using ObjectAnalyzer.Analysis.Rules;
using ObjectAnalyzer.Parsing;

namespace ObjectAnalyzer.Api;

/// <summary>
/// 分析端点：POST /api/analyze（multipart 上传）、POST /api/analyze/config（强类型配置）、POST /api/analyze/raw（请求体即数据文本）。
/// </summary>
public static class AnalyzeEndpoints
{
    public static IEndpointRouteBuilder MapAnalyzeApi(this IEndpointRouteBuilder routes)
    {
        // ── 分析：multipart 上传 ──
        routes.MapPost("/analyze",
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
                // 规则带 valueParsers 时实际走的是 excel-json 解析器，响应里如实回写，
                // 免得 UI 显示 "excel" 而字段却已按 JSON 展开，看着像抽风
                var effectiveFormat = AnalysisCore.ResolveEffectiveFormat(format, rulesObj);
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
                    Format: effectiveFormat,
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

        // ── 分析：强类型配置（数据文件上传，其余全部走强类型 config JSON）──
        // 与 /api/analyze 相比：除数据文件 file 外，所有选项与规则合并为一个强类型 config JSON
        // （options + 内联 rules 对象 + P4 开关），不再需要规则文件/规则字符串，也不散成逐项表单/查询参数。
        routes.MapPost("/analyze/config",
            async Task<Results<Ok<AnalyzeResponse>, BadRequest<ErrorResponse>>> (
            [FromForm] AnalyzeConfigForm form,
            CancellationToken cancellationToken) =>
        {
            var file = form.File;
            if (file is null || file.Length == 0)
                return TypedResults.BadRequest(new ErrorResponse("请通过 multipart 表单上传数据文件（字段名 file）。"));

            if (string.IsNullOrWhiteSpace(form.Config))
                return TypedResults.BadRequest(new ErrorResponse("缺少强类型配置：请通过 multipart 表单字段 config 传入 JSON（选项 + rules 规则对象）。"));

            AnalyzeConfigRequest config;
            try
            {
                // 与规则同款：允许 // 注释、字段名大小写不敏感；配置 JSON 里嵌套的 rules 走各自 [JsonPropertyName]。
                config = JsonSerializer.Deserialize<AnalyzeConfigRequest>(form.Config, AnalysisRules.RulesJsonOptions)
                         ?? throw new InvalidDataException("config 反序列化结果为空。");
            }
            catch (JsonException ex)
            {
                return TypedResults.BadRequest(new ErrorResponse($"config 不是合法 JSON：{ex.Message}"));
            }

            var tempData = Path.Combine(Path.GetTempPath(), $"oa-api-{Guid.NewGuid():N}{Path.GetExtension(file.FileName)}");
            try
            {
                await using (var fs = File.Create(tempData))
                    await file.CopyToAsync(fs, cancellationToken);

                var format = FileParsers.DetectFormat(tempData);
                var effectiveFormat = AnalysisCore.ResolveEffectiveFormat(format, config.Rules);
                var result = AnalysisCore.AnalyzeFile(tempData, format, config.Rules, config, cancellationToken);

                return TypedResults.Ok(new AnalyzeResponse(
                    FileName: file.FileName,
                    Format: effectiveFormat,
                    RulesSource: config.Rules is not null ? "inline" : null,
                    Result: result));
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
        .WithSummary("分析数据文件（强类型配置）")
        .WithDescription("上传数据文件 file，其余所有选项与规则放进单个强类型 config JSON（multipart 表单字段）。" +
            "config 结构：继承全部分析选项（flatten/csvInferNumbers/fields/filter/sample*/maxParallel 等）+ P4 开关，" +
            "并内含强类型 rules 规则对象（flatten/valueParsers/fields/filter/expectations）——规则不是文件、不是字符串。" +
            "config 支持 // 注释、字段名大小写不敏感。响应与 /api/analyze 一致（camelCase，字段级违规内嵌于 result.fieldStatistics）。")
        .DisableAntiforgery();

        // ── 分析：请求体即数据文本 ──
        routes.MapPost("/analyze/raw",
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
            [Description("内联规则 JSON（走查询参数，受请求行长度限制；规则较长请改用 multipart 上传）")] string? rulesJson,
            [Description("容忍规则里不认识的键（默认 false，拼错会报错）")] bool? allowUnknownRules,
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

                // 规则走查询参数（请求体已被数据文本占用）：与 multipart 版本同源的未知键检查，
                // 避免"配了规则却静默不生效"。反序列化失败由外层 catch 统一转 400。
                AnalysisRules? rulesObj = null;
                if (!string.IsNullOrWhiteSpace(rulesJson))
                {
                    if (allowUnknownRules is not true)
                    {
                        var unknownRaw = RulesLint.Scan(rulesJson);
                        if (unknownRaw.Count > 0)
                            return TypedResults.BadRequest(ApiErrors.UnknownRules(unknownRaw));
                    }

                    rulesObj = JsonSerializer.Deserialize<AnalysisRules>(rulesJson, AnalysisRules.RulesJsonOptions);
                }

                var requestedFormat = format.Trim().ToLowerInvariant();
                // 规则带 valueParsers 时实际走 excel-json 解析器，响应里如实回写
                var effectiveFormat = AnalysisCore.ResolveEffectiveFormat(requestedFormat, rulesObj);

                var result = AnalysisCore.AnalyzeFile(tempData, requestedFormat, rulesObj,
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

                return TypedResults.Ok(new AnalyzeRawResponse(
                    effectiveFormat, rulesObj is null ? null : "inline", result));
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
        .WithDescription("请求体即数据文本（JSON / JSONL / CSV / Excel / XML / YAML），通过 ?format= 指定格式；全部分析参数通过查询字符串传入。响应与 multipart 版本一致。" +
            "规则通过 ?rulesJson= 传入（请求体已被数据文本占用，故只能走查询参数 —— 受请求行长度限制，" +
            "规则较长时请改用 multipart 的 /api/analyze）。规则里的未知键默认同样拦截并返回 400 + unknowns，" +
            "确认可忽略时追加 allowUnknownRules=true。" +
            "规则含 valueParsers 时实际按 excel-json 解析，响应的 format 会如实回写 excel-json。")
        .DisableAntiforgery();

        return routes;
    }
}
