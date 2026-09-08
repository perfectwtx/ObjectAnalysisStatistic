using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using ObjectAnalyzer.Analysis;
using ObjectAnalyzer.Analysis.Rules;

namespace ObjectAnalyzer.Api;

/// <summary>
/// 规则相关端点：POST /api/rules/validate（校验规则 JSON）与 GET /api/rules/reference（参考模板）。
/// </summary>
public static class RulesEndpoints
{
    public static IEndpointRouteBuilder MapRulesApi(this IEndpointRouteBuilder routes)
    {
        // ── 规则校验 ──
        // 除反序列化本身的错误（结构写错、类型写错）外，额外列出「写了但后端不认识」的键：
        // STJ 默认静默忽略未知成员，规则名拼错时用户得不到任何反馈，规则看似生效实则完全没生效。
        // 所以未知键默认直接算校验不通过（valid=false），调用方据此阻断分析；
        // 确实要放行时追加 ?strict=false，退回只提示不拦截。
        routes.MapPost("/rules/validate",
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
        var env = routes.ServiceProvider.GetRequiredService<IWebHostEnvironment>();
        routes.MapGet("/rules/reference", () =>
        {
            // 依次尝试：ContentRoot/samples → 仓库根 samples（开发布局）
            string[] candidates =
            [
                Path.Combine(env.ContentRootPath, "samples", "rules_full_reference.jsonc"),
                Path.Combine(env.ContentRootPath, "..", "..", "samples", "rules_full_reference.jsonc")
            ];
            var path = candidates.FirstOrDefault(File.Exists);
            return path is null
                ? Results.NotFound(new { error = "参考模板文件不存在（samples/rules_full_reference.jsonc）" })
                : Results.Text(File.ReadAllText(path), "application/json");
        })
        .WithTags("规则")
        .WithSummary("返回全量规则参考模板")
        .WithDescription("返回 samples/rules_full_reference.jsonc 的完整内容——覆盖全部规则配置且逐项带中文注释，可作为规则文件的起点。");

        return routes;
    }
}
