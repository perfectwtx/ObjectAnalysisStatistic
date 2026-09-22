using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using ObjectAnalyzer.Platform;

namespace ObjectAnalyzer.Api;

/// <summary>
/// 删除分析历史作业。规范路径：DELETE /api/analysis/jobs/{jobId}。
/// </summary>
public static class AnalysisJobDeleteEndpoints
{
    public static IEndpointRouteBuilder MapAnalysisJobDeleteApi(this IEndpointRouteBuilder routes)
    {
        routes.MapDelete("/analysis/jobs/{jobId:guid}", DeleteJob)
            .WithTags("分析历史")
            .WithSummary("删除分析作业及其关联质量数据");

        // 与前端 tryPaths 对齐的别名
        routes.MapDelete("/jobs/{jobId:guid}", DeleteJob)
            .WithTags("分析历史")
            .WithSummary("删除分析作业（别名）");

        routes.MapDelete("/analysis/jobs", DeleteJobs)
            .WithTags("分析历史")
            .WithSummary("批量删除分析作业");

        routes.MapDelete("/jobs", DeleteJobs)
            .WithTags("分析历史")
            .WithSummary("批量删除分析作业（别名）");

        return routes;
    }

    private static async Task<Results<NoContent, NotFound<ErrorResponse>, Conflict<ErrorResponse>>> DeleteJob(
        Guid jobId,
        IAnalysisHistoryStore store,
        CancellationToken cancellationToken)
    {
        var job = await store.GetAsync(jobId, cancellationToken);
        if (job is null)
            return TypedResults.NotFound(new ErrorResponse($"未找到分析作业：{jobId}"));

        if (job.Status is AnalysisJobStatus.Pending or AnalysisJobStatus.Running)
            return TypedResults.Conflict(new ErrorResponse("作业仍在进行中，请先取消后再删除。"));

        var removed = await store.DeleteAsync(jobId, cancellationToken);
        return removed
            ? TypedResults.NoContent()
            : TypedResults.NotFound(new ErrorResponse($"未找到分析作业：{jobId}"));
    }

    private static async Task<Results<Ok<DeleteJobsResponse>, BadRequest<ErrorResponse>>> DeleteJobs(
        [FromBody] DeleteJobsRequest? body,
        IAnalysisHistoryStore store,
        CancellationToken cancellationToken)
    {
        var ids = body?.Ids?.Where(x => x != Guid.Empty).Distinct().ToList() ?? new List<Guid>();
        if (ids.Count == 0)
            return TypedResults.BadRequest(new ErrorResponse("请提供要删除的作业 id 列表（body.ids）。"));

        var deleted = new List<Guid>();
        var failed = new List<DeleteJobFailure>();

        foreach (var id in ids)
        {
            var job = await store.GetAsync(id, cancellationToken);
            if (job is null)
            {
                failed.Add(new DeleteJobFailure(id, "not_found", "作业不存在"));
                continue;
            }

            if (job.Status is AnalysisJobStatus.Pending or AnalysisJobStatus.Running)
            {
                failed.Add(new DeleteJobFailure(id, "running", "作业仍在进行中"));
                continue;
            }

            var ok = await store.DeleteAsync(id, cancellationToken);
            if (ok) deleted.Add(id);
            else failed.Add(new DeleteJobFailure(id, "not_found", "作业不存在"));
        }

        return TypedResults.Ok(new DeleteJobsResponse(deleted.Count, deleted, failed));
    }

    public sealed record DeleteJobsRequest(IReadOnlyList<Guid>? Ids);

    public sealed record DeleteJobsResponse(
        int Deleted,
        IReadOnlyList<Guid> Ids,
        IReadOnlyList<DeleteJobFailure> Failed);

    public sealed record DeleteJobFailure(Guid Id, string Code, string Message);
}
