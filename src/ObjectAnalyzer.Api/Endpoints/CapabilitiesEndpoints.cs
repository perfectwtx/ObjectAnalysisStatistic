using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using ObjectAnalyzer.Platform;

namespace ObjectAnalyzer.Api;

/// <summary>返回当前 API 部署支持的能力清单，供前端决定菜单和真实 API / mock 切换。</summary>
public static class CapabilitiesEndpoints
{
    public static IEndpointRouteBuilder MapCapabilitiesApi(this IEndpointRouteBuilder routes)
    {
        Map(routes.MapGet("/capabilities", GetCapabilities));
        Map(routes.MapGet("/meta/capabilities", GetCapabilities));
        return routes;

        static void Map(RouteHandlerBuilder endpoint) =>
            endpoint
                .WithTags("系统")
                .WithSummary("获取当前 API 能力清单")
                .WithDescription("返回当前部署已注册并可供前端使用的模块。");

        static Ok<CapabilitiesResponse> GetCapabilities() =>
            TypedResults.Ok(new CapabilitiesResponse(
                Analyze: true,
                AsyncAnalyze: true,
                Preflight: true,
                QualityDashboard: true,
                QualityTrend: true,
                Issues: true,
                IssueOperations: true,
                Baselines: true,
                AlertRules: true,
                Webhooks: true,
                Lineage: true,
                Impact: true,
                Relationships: true,
                SchemaImpact: true,
                Recommendations: true,
                Regression: true,
                Export: true,
                Rules: true,
                Health: true));
    }

    public sealed record CapabilitiesResponse(
        bool Analyze,
        bool AsyncAnalyze,
        bool Preflight,
        bool QualityDashboard,
        bool QualityTrend,
        bool Issues,
        bool IssueOperations,
        bool Baselines,
        bool AlertRules,
        bool Webhooks,
        bool Lineage,
        bool Impact,
        bool Relationships,
        bool SchemaImpact,
        bool Recommendations,
        bool Regression,
        bool Export,
        bool Rules,
        bool Health);
}
