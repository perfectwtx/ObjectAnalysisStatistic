using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using ObjectAnalyzer.Platform;

namespace ObjectAnalyzer.Api;

/// <summary>数据质量基线管理端点。</summary>
public static class QualityBaselineEndpoints
{
    public static IEndpointRouteBuilder MapQualityBaselineApi(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/quality-baselines",
            async Task<Ok<IReadOnlyList<QualityBaseline>>> (IQualityBaselineStore store, CancellationToken cancellationToken) =>
                TypedResults.Ok(await store.GetAllAsync(cancellationToken)))
            .WithTags("质量基线")
            .WithSummary("获取全部质量基线");

        routes.MapGet("/quality-baselines/{sourceName}",
            async Task<Results<Ok<QualityBaseline>, NotFound<ErrorResponse>>> (
                string sourceName,
                IQualityBaselineStore store,
                CancellationToken cancellationToken) =>
            {
                if (string.IsNullOrWhiteSpace(sourceName))
                    return TypedResults.NotFound(new ErrorResponse("数据源名称不能为空。"));

                var baseline = await store.GetAsync(sourceName, cancellationToken);
                return baseline is null
                    ? TypedResults.NotFound(new ErrorResponse($"未找到数据源“{sourceName}”的质量基线。"))
                    : TypedResults.Ok(baseline);
            })
            .WithTags("质量基线")
            .WithSummary("获取指定数据源的质量基线");

        routes.MapPut("/quality-baselines/{sourceName}",
            async Task<Results<Ok<QualityBaseline>, BadRequest<ErrorResponse>>> (
                string sourceName,
                QualityBaselineRequest request,
                IQualityBaselineStore store,
                CancellationToken cancellationToken) =>
            {
                var baseline = request with { SourceName = sourceName }.ToModel();
                try
                {
                    baseline.Validate();
                    await store.SaveAsync(baseline, cancellationToken);
                    return TypedResults.Ok(baseline);
                }
                catch (ArgumentException ex)
                {
                    return TypedResults.BadRequest(new ErrorResponse(ex.Message));
                }
            })
            .WithTags("质量基线")
            .WithSummary("创建或更新质量基线")
            .WithDescription("数据源名称来自 URL；同一数据源重复保存会覆盖原基线。所有阈值均为 0 到 100 的百分制。");

        routes.MapDelete("/quality-baselines/{sourceName}",
            async Task<NoContent> (
                string sourceName,
                IQualityBaselineStore store,
                CancellationToken cancellationToken) =>
            {
                await store.DeleteAsync(sourceName, cancellationToken);
                return TypedResults.NoContent();
            })
            .WithTags("质量基线")
            .WithSummary("删除质量基线");

        return routes;
    }
}
