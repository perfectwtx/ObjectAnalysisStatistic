using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using ObjectAnalyzer.Analysis;
using ObjectAnalyzer.Platform;

namespace ObjectAnalyzer.Api;

public static partial class AsyncAnalyzeEndpoints
{
    private static void MapCancelEndpoint(IEndpointRouteBuilder routes)
    {
        // ── 取消作业 ──
        routes.MapPost("/analyze/async/{jobId}/cancel",
            async Task<Results<Ok<AnalyzeCancelResponse>, NotFound<ErrorResponse>, Conflict<ErrorResponse>>> (
                string jobId,
                IAnalysisHistoryStore historyStore,
                CancellationToken cancellationToken) =>
        {
            // 先读取持久化状态，避免分析已经完成后，旧的 Registry 快照仍被当成可取消。
            if (Guid.TryParse(jobId, out var persistedJobId))
            {
                jobId = persistedJobId.ToString("N");
                var job = await historyStore.GetAsync(persistedJobId, cancellationToken);
                if (job is not null)
                {
                    if (job.Status is AnalysisJobStatus.Succeeded or AnalysisJobStatus.Failed or AnalysisJobStatus.Cancelled)
                        return TypedResults.Ok(new AnalyzeCancelResponse(jobId, false));

                    var ok = AnalysisJobRegistry.Instance.Cancel(jobId);
                    var snap = AnalysisJobRegistry.Instance.Get(jobId);
                    if (snap is not null)
                        return TypedResults.Ok(new AnalyzeCancelResponse(jobId, ok));

                    return TypedResults.Conflict(new ErrorResponse("作业仍在运行，但当前进程中已找不到对应的运行时取消句柄，无法执行取消。"));
                }
            }

            return TypedResults.NotFound(new ErrorResponse($"作业不存在或已清理：{jobId}"));
        })
        .WithTags("异步分析")
        .WithSummary("取消异步分析作业")
        .WithDescription("触发对应作业的 CancellationTokenSource。运行时作业存在时返回 cancelled=true/false；运行时状态已清理但持久化作业已终态时返回 cancelled=false；持久化作业仍在运行但缺少运行时句柄时返回 409。");
    }
}
