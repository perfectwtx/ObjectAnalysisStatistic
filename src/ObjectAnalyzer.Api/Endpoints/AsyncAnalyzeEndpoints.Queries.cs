using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using ObjectAnalyzer.Analysis;
using ObjectAnalyzer.Platform;

namespace ObjectAnalyzer.Api;

public static partial class AsyncAnalyzeEndpoints
{
    private static void MapQueryEndpoints(IEndpointRouteBuilder routes)
    {
        // ── 查询作业状态 / 拉取结果 ──
        routes.MapGet("/analyze/async/{jobId}",
            async Task<Results<Ok<AnalyzeJobSnapshotResponse>, NotFound<ErrorResponse>>> (
                string jobId,
                IAnalysisHistoryStore historyStore,
                CancellationToken cancellationToken) =>
        {
            // 持久化 Job 是最终状态的权威来源。运行中的作业仍优先使用 Registry，
            // 以获得实时进度；一旦持久化状态已经进入终态，就不能再被旧的 Registry 快照覆盖。
            if (Guid.TryParse(jobId, out var persistedJobId))
            {
                var job = await historyStore.GetAsync(persistedJobId, cancellationToken);
                if (job is not null)
                {
                    if (job.Status is AnalysisJobStatus.Succeeded or AnalysisJobStatus.Failed or AnalysisJobStatus.Cancelled)
                        return TypedResults.Ok(ToResponse(job, AnalysisJobRegistry.Instance.Get(jobId)));

                    var runningSnap = AnalysisJobRegistry.Instance.Get(jobId);
                    if (runningSnap is not null)
                        return TypedResults.Ok(ToResponse(runningSnap));

                    return TypedResults.Ok(ToResponse(job));
                }
            }

            // Missing persistence is authoritative: deleted jobs must never reappear from cache.
            return TypedResults.NotFound(new ErrorResponse($"作业不存在或已清理：{jobId}"));
        })
        .WithTags("异步分析")
        .WithSummary("查询异步作业状态")
        .WithDescription("""
        返回 AnalysisJobSnapshot：状态、提交时间、终态时间、进度、结果或错误（仅 type+message）。
        - 状态 Running 时 Result/Error 为 null；
        - Completed 时 Result 携带完整 AnalysisResult；
        - Failed 时 Error 含异常类型与消息；
        - Cancelled 是用户主动中断的状态。
        """);

        // ── SSE 事件流 ──
        routes.MapGet("/analyze/async/{jobId}/events", async (string jobId, HttpContext http, IAnalysisHistoryStore historyStore) =>
        {
            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers.Connection = "keep-alive";

            async Task WriteEventAsync(string eventName, string json, CancellationToken ct)
            {
                await http.Response.WriteAsync($"event: {eventName}\n", ct);
                await http.Response.WriteAsync($"data: {json}\n\n", ct);
                await http.Response.Body.FlushAsync(ct);
            }

            static string SnapJson(AnalysisJobSnapshot snap) => JsonSerializer.Serialize(new
            {
                processedObjects = snap.ProcessedObjects,
                status = snap.Status.ToString(),
                error = snap.Error is { } e ? $"{e.GetType().Name}: {e.Message}" : null
            }, s_sseJsonOptions);

            try
            {
                var ct = http.RequestAborted;
                while (true)
                {
                    if (!Guid.TryParse(jobId, out var persistedJobId))
                    {
                        await WriteEventAsync("gone", "{\"reason\":\"invalid job id\"}", ct);
                        return;
                    }
                    var persisted = await historyStore.GetAsync(persistedJobId, ct);
                    if (persisted is null)
                    {
                        await WriteEventAsync("gone", "{\"reason\":\"job not found\"}", ct);
                        return;
                    }
                    var terminal = persisted.Status is not (AnalysisJobStatus.Pending or AnalysisJobStatus.Running);
                    if (terminal)
                    {
                        var response = ToResponse(persisted);
                        await WriteEventAsync("complete", JsonSerializer.Serialize(new
                        {
                            processedObjects = response.ProcessedObjects,
                            status = response.Status.ToString(), error = response.Error
                        }, s_sseJsonOptions), ct);
                        return;
                    }
                    var snap = AnalysisJobRegistry.Instance.Get(jobId);
                    if (snap is not null)
                        await WriteEventAsync(snap.Status == AnalysisStatus.Running ? "progress" : "complete", SnapJson(snap), ct);
                    else
                        await WriteEventAsync("progress", JsonSerializer.Serialize(new
                        {
                            processedObjects = persisted.TotalObjects, status = "Running", error = (string?)null
                        }, s_sseJsonOptions), ct);
                    if (snap is not null && snap.Status != AnalysisStatus.Running) return;

                    await Task.Delay(500, ct);
                }
            }
            catch (OperationCanceledException)
            {
                // 客户端断开：正常结束 SSE 流
            }
        })
        .WithTags("异步分析")
        .WithSummary("订阅异步作业事件流（SSE）")
        .WithDescription("""
        text/event-stream 事件流：progress / complete / gone。
        结果本体不随事件推送，收到 complete 后应 GET /{jobId} 拉取结果。
        """);
    }
}
