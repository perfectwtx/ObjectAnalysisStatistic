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
            async Task<Results<Ok<AnalyzeViewResponse>, BadRequest<ErrorResponse>>> (
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

                // 规则带 valueParsers 时实际走的是 excel-json 解析器，响应里如实回写，
                // 免得 UI 显示 "excel" 而字段却已按 JSON 展开，看着像抽风
                var effectiveFormat = AnalysisCore.ResolveEffectiveFormat(tempData, rulesObj, formatOverride: null);
                // multipart 表单字段通过继承复用，AnalysisOptions 一次性构造（含 rules.runtime 合并）
                var options = form.ToAnalysisOptions(tempData, rulesObj);
                var outcome = AnalysisCore.AnalyzeFile(tempData, rulesObj, options, cancellationToken);

                // W8 五页面视图：概览 / 预览 / 字段 / 深度分析 / 质量 + 完整 result 兼容保留
                return TypedResults.Ok(AnalyzeViewResponse.From(
                    outcome.Result, outcome.Preview, effectiveFormat,
                    fileName: file.FileName, rulesSource: rulesSource,
                    piiRedacted: outcome.PiiRedacted, pii: outcome.Pii));
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
            "响应（含数据预览）默认已按 PII 策略出站打码；传 piiOff=true 显式关闭，响应以 piiRedacted=false 留痕。" +
            "规则里若出现后端不认识的键（拼错/已废弃），默认直接返回 400 并列出 unknowns，" +
            "避免规则看似生效实则被静默忽略；确认无需处理时传 allowUnknownRules=true。" +
            "响应为 camelCase JSON，字段级违规内嵌于 result.fieldStatistics。")
        .DisableAntiforgery();

        // ── 分析：强类型 JSON body（数据文本内嵌 + 全强类型配置）──
        // 请求体为纯 JSON，读取原始体后先对内联 rules 子树做未知键检查（与 multipart/raw 同源，
        // 强类型绑定会静默丢弃不认识的键），再反序列化为强类型 AnalyzeConfigRequest：
        // 数据文本走 dataText（JSON/JSONL/CSV/XML/YAML），其余全部是强类型字段——
        // 分析选项（flatten/csvInferNumbers/fields/filter/sample*/maxParallel + 深度分析开关）+ 内联 rules 规则对象。
        // OpenAPI / Scalar 能看到每个配置字段及其 rules.* 子结构。
        routes.MapPost("/analyze/config",
            async Task<Results<Ok<AnalyzeViewResponse>, BadRequest<ErrorResponse>>> (
            HttpRequest request,
            CancellationToken cancellationToken) =>
        {
            var (config, configError) = await AnalysisCore.ReadConfigRequestAsync(request, cancellationToken);
            if (configError is not null)
                return TypedResults.BadRequest(configError);

            if (string.IsNullOrWhiteSpace(config!.DataText))
                return TypedResults.BadRequest(new ErrorResponse("缺少数据文本：请提供 dataText（数据内容，JSON / JSONL / CSV / XML / YAML）。"));

            var tempData = Path.Combine(Path.GetTempPath(), $"oa-api-{Guid.NewGuid():N}.txt");
            try
            {
                await File.WriteAllTextAsync(tempData, config.DataText, cancellationToken);

                var effectiveFormat = AnalysisCore.ResolveEffectiveFormat(tempData, config.Rules, config.Format);
                var options = config.ToAnalysisOptions(tempData, config.Rules);
                // 显式格式必须透传给管线（异步 config 端点同此）：.txt 临时文件无法按扩展名探测，
                // 漏设会回落到 JSONL 解析器，数组型 dataText 会误报"根节点必须是一个对象"
                options.Input.Format = config.Format;
                var outcome = AnalysisCore.AnalyzeFile(tempData, config.Rules, options, cancellationToken);

                return TypedResults.Ok(AnalyzeViewResponse.From(
                    outcome.Result, outcome.Preview, effectiveFormat,
                    fileName: "(inline)", rulesSource: config.Rules is not null ? "inline" : null,
                    piiRedacted: outcome.PiiRedacted, pii: outcome.Pii));
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
        .Accepts<AnalyzeConfigRequest>("application/json")
        .WithTags("分析")
        .WithSummary("分析强类型 JSON 请求体")
        .WithDescription("请求体为纯 JSON，直接绑定为强类型 AnalyzeConfigRequest：dataText 内嵌数据文本（JSON/JSONL/CSV/XML/YAML），" +
            "format 指定格式（留空自动检测）。其余全部是强类型字段——分析选项（flatten/csvInferNumbers/fields/filter/sample*/maxParallel + 深度分析开关）" +
            "与内联 rules 规则对象（flatten/valueParsers/fields/filter/expectations）。" +
            "OpenAPI / Scalar 能看到每个配置字段及其 rules.* 子结构。响应为 camelCase JSON，字段级违规内嵌于 result.fieldStatistics。")
        .DisableAntiforgery();

        // ── 分析：请求体即数据文本 ──
        // 分析参数统一通过 [AsParameters] AnalyzeRequestOptions 绑定（每个属性 → 独立查询参数），
        // 规则通过查询参数 rulesJson / allowUnknownRules 传入。
        // 这样查询参数集合与 /analyze、/analyze/config 的字段一一对应，新增选项时三个端点同步。
        routes.MapPost("/analyze/raw",
            async Task<Results<Ok<AnalyzeViewResponse>, BadRequest<ErrorResponse>>> (
            HttpRequest request,
            [AsParameters] AnalyzeRequestOptions options,
            [Description("数据格式：json / jsonl / csv / excel / xml / yaml")] string format,
            [Description("内联规则 JSON（走查询参数，受请求行长度限制；规则较长请改用 multipart 上传）")] string? rulesJson,
            [Description("容忍规则里不认识的键（默认 false，拼错会报错）")] bool? allowUnknownRules,
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
                var effectiveFormat = AnalysisCore.ResolveEffectiveFormat(tempData, rulesObj, requestedFormat);

                // 查询参数 + 规则一次性构造 AnalysisOptions（含 rules.runtime 合并）
                var analysisOptions = options.ToAnalysisOptions(tempData, rulesObj);
                analysisOptions.Input.Format = requestedFormat;
                var outcome = AnalysisCore.AnalyzeFile(tempData, rulesObj, analysisOptions, cancellationToken);

                return TypedResults.Ok(AnalyzeViewResponse.From(
                    outcome.Result, outcome.Preview, effectiveFormat,
                    fileName: "(inline)", rulesSource: rulesObj is null ? null : "inline",
                    piiRedacted: outcome.PiiRedacted, pii: outcome.Pii));
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
        .WithDescription("请求体即数据文本（JSON / JSONL / CSV / Excel / XML / YAML），通过 ?format= 指定格式；全部分析参数通过查询字符串传入（与 /analyze、/analyze/config 字段一致），" +
            "规则通过 ?rulesJson= 传入（请求体已被数据文本占用，故只能走查询参数 —— 受请求行长度限制，" +
            "规则较长时请改用 multipart 的 /api/analyze）。规则里的未知键默认同样拦截并返回 400 + unknowns，" +
            "确认可忽略时追加 allowUnknownRules=true。" +
            "规则含 valueParsers 时实际按 excel-json 解析，响应的 format 会如实回写 excel-json。")
        .DisableAntiforgery();

        return routes;
    }
}
