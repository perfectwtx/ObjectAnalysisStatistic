using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using ObjectAnalyzer.Analysis;

namespace ObjectAnalyzer.Api;

/// <summary>GET /api/health 健康检查端点。</summary>
public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthApi(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/health", () =>
        {
            var version = typeof(AnalysisResult).Assembly.GetName().Version?.ToString();
            return TypedResults.Ok(new HealthResponse("ok", version));
        })
        .WithTags("系统")
        .WithSummary("健康检查")
        .WithDescription("返回服务状态与核心分析库版本号。");

        return routes;
    }
}
