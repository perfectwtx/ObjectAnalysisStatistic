using System.ComponentModel;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using System.Text.Json;
using ObjectAnalyzer.Analysis;
using ObjectAnalyzer.Parsing;

namespace ObjectAnalyzer.Api;

/// <summary>
/// 异步分析端点：
/// - POST /api/analyze/async                   提交分析作业，返回 jobId
/// - GET  /api/analyze/async/{jobId}           查询作业状态 / 拉取结果
/// - POST /api/analyze/async/{jobId}/cancel    取消正在进行的分析
///
/// 作业生命周期由 AnalysisJobRegistry（Core）统一管理：
/// - 注册时拿到 CancellationTokenSource，传给 AnalysisRunner；
/// - 完成后写回 AnalysisResult；取消时触发 CTS。
///
/// 数据库 / 非文件型输入场景不支持取消；这些请求一律走同步端点 /api/analyze。
/// </summary>
public static class AsyncAnalyzeEndpoints
{
    // SSE 事件 payload 的序列化选项：camelCase + 非 null 输出（与 API 契约同风格）
    private static readonly JsonSerializerOptions s_sseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public static IEndpointRouteBuilder MapAsyncAnalyzeApi(this IEndpointRouteBuilder routes)
    {
        // ── 异步分析：multipart 上传 ──
        routes.MapPost("/analyze/async",
            async Task<Results<Ok<AnalyzeAsyncResponse>, BadRequest<ErrorResponse>>> (
            [FromForm] AnalyzeForm form,
            HttpRequest request,
            CancellationToken cancellationToken) =>
        {
            var file = form.File;
            if (file is null || file.Length == 0)
                return TypedResults.BadRequest(new ErrorResponse("请通过 multipart 表单上传数据文件（字段名 file）。"));

            var tempData = Path.Combine(Path.GetTempPath(), $"oa-async-{Guid.NewGuid():N}{Path.GetExtension(file.FileName)}");
            var tempRules = Path.Combine(Path.GetTempPath(), $"oa-async-{Guid.NewGuid():N}.json");
            string? jobId = null;
            var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            try
            {
                await using (var fs = File.Create(tempData))
                    await file.CopyToAsync(fs, cancellationToken);

                AnalysisRules? rulesObj = null;
                var rulesSource = form.RulesJson is not null ? "inline" : form.Rules is not null ? "file" : null;

                string? rulesText = form.RulesJson;
                if (rulesText is null && form.Rules is not null && form.Rules.Length > 0)
                {
                    await using (var rfs = File.Create(tempRules))
                        await form.Rules.CopyToAsync(rfs, cancellationToken);
                    rulesText = await File.ReadAllTextAsync(tempRules, cancellationToken);
                }

                if (rulesText is not null)
                {
                    var (rules, err) = AnalysisCore.ParseRules(rulesText, form.AllowUnknownRules is true);
                    if (err is not null) return TypedResults.BadRequest(err);
                    rulesObj = rules;
                }

                var effectiveFormat = AnalysisCore.ResolveEffectiveFormat(tempData, rulesObj, formatOverride: null);
                var options = form.ToAnalysisOptions(tempData, rulesObj);

                // 注册异步作业；AnalysisJobRegistry 会持有 CancellationTokenSource，
                // 客户端可随时通过 /cancel 端点触发取消。
                jobId = AnalysisJobRegistry.Instance.Register(
                    cts,
                    new AnalysisJobMeta(
                        DataFileName: file.FileName,
                        Format: effectiveFormat,
                        OperatorId: null,
                        SubmittedAt: DateTimeOffset.UtcNow));

                // 临时文件的所有权交给后台任务：它执行结束（成功 / 失败 / 取消）后自行清理。
                // 这样 finally 块就不会在任务读取前把文件删掉了。
                // 关键：必须先把路径复制到 lambda 的局部变量再启动任务。
                // 闭包按引用捕获外层变量，若直接传 tempData / tempRules，Task.Run 实际执行
                // 时主线程可能已经把这两个变量置为 null（NRE 的根因：CompressionDetector.Detect(null)）。
                var jobIdForTask = jobId;
                var dataPathForTask = tempData;
                var rulesPathForTask = tempRules;
                _ = Task.Run(() => RunJob(jobIdForTask, dataPathForTask, rulesPathForTask, rulesObj, options, cts.Token),
                    CancellationToken.None);
                // 任务已接手这两个路径，避免下面 catch/finally 再去删。
                tempData = null!;
                tempRules = null!;

                return TypedResults.Ok(new AnalyzeAsyncResponse(
                    JobId: jobId,
                    Status: AnalysisStatus.Running,
                    FileName: file.FileName,
                    Format: effectiveFormat,
                    RulesSource: rulesSource,
                    SubmittedAt: DateTimeOffset.UtcNow));
            }
            catch (Exception ex)
            {
                // 作业未注册成功 → 释放 CTS 并清理临时文件
                try { cts.Cancel(); } catch { }
                try { cts.Dispose(); } catch { }
                if (jobId is not null) AnalysisJobRegistry.Instance.Fail(jobId, ex);
                TryDelete(tempData);
                TryDelete(tempRules);
                return TypedResults.BadRequest(ApiErrors.FromException(ex));
            }
        })
        .WithTags("异步分析")
        .WithSummary("异步分析（multipart 上传）")
        .WithDescription("""
        提交后立即返回 jobId，分析在后台运行。
        - GET  /api/analyze/async/{jobId}           轮询状态 / 拉取结果
        - POST /api/analyze/async/{jobId}/cancel    触发取消
        作业保留 5 分钟（AnalysisJobRegistry.DefaultRetention），期间结果可被拉取。
        """)
        .DisableAntiforgery();

        // ── 异步分析：强类型 JSON body ──
        routes.MapPost("/analyze/async/config",
            async Task<Results<Ok<AnalyzeAsyncResponse>, BadRequest<ErrorResponse>>> (
            [FromBody] AnalyzeConfigRequest config,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(config.DataText))
                return TypedResults.BadRequest(new ErrorResponse("缺少数据文本：请提供 dataText。"));

            var tempData = Path.Combine(Path.GetTempPath(), $"oa-async-{Guid.NewGuid():N}.txt");
            string? jobId = null;
            var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            try
            {
                await File.WriteAllTextAsync(tempData, config.DataText, cancellationToken);

                var effectiveFormat = AnalysisCore.ResolveEffectiveFormat(tempData, config.Rules, config.Format);
                var options = config.ToAnalysisOptions(tempData, config.Rules);
                options.Input.Format = config.Format;

                jobId = AnalysisJobRegistry.Instance.Register(
                    cts,
                    new AnalysisJobMeta(
                        DataFileName: "(inline)",
                        Format: effectiveFormat,
                        OperatorId: null,
                        SubmittedAt: DateTimeOffset.UtcNow));

                var jobIdForTask = jobId;
                // 同 multipart 端点：路径必须先复制到 lambda 局部变量，否则闭包按引用捕获
                // 的外层 tempData 在主线程置 null 后会让后台任务拿到空字符串 → NRE。
                var dataPathForTask = tempData;
                _ = Task.Run(() => RunJob(jobIdForTask, dataPathForTask, null, config.Rules, options, cts.Token),
                    CancellationToken.None);
                tempData = null!;

                return TypedResults.Ok(new AnalyzeAsyncResponse(
                    JobId: jobId,
                    Status: AnalysisStatus.Running,
                    FileName: "(inline)",
                    Format: effectiveFormat,
                    RulesSource: config.Rules is not null ? "inline" : null,
                    SubmittedAt: DateTimeOffset.UtcNow));
            }
            catch (Exception ex)
            {
                try { cts.Cancel(); } catch { }
                try { cts.Dispose(); } catch { }
                if (jobId is not null) AnalysisJobRegistry.Instance.Fail(jobId, ex);
                TryDelete(tempData);
                return TypedResults.BadRequest(ApiErrors.FromException(ex));
            }
        })
        .WithTags("异步分析")
        .WithSummary("异步分析（强类型 JSON 请求体）")
        .DisableAntiforgery();

        // ── 查询作业状态 / 拉取结果 ──
        routes.MapGet("/analyze/async/{jobId}",
            Results<Ok<AnalyzeJobSnapshotResponse>, NotFound<ErrorResponse>> (string jobId) =>
        {
            var snap = AnalysisJobRegistry.Instance.Get(jobId);
            if (snap is null)
                return TypedResults.NotFound(new ErrorResponse($"作业不存在或已清理：{jobId}"));
            // 错误信息序列化为字符串：Exception 内部含 MethodBase/StackTrace 等不可 JSON 化的字段，
            // 直接走 STJ 会触发 NotSupportedException。这里只回显 type+message 供前端展示。
            string? errorMessage = null;
            if (snap.Error is { } err)
            {
                errorMessage = $"{err.GetType().Name}: {err.Message}";
            }
            return TypedResults.Ok(new AnalyzeJobSnapshotResponse(
                Id: snap.Id,
                Status: snap.Status,
                Meta: snap.Meta,
                CreatedAt: snap.CreatedAt,
                TerminalAt: snap.TerminalAt,
                ProgressText: snap.ProgressText,
                ProcessedObjects: snap.ProcessedObjects,
                Result: snap.Result,
                Preview: snap.Preview,
                Error: errorMessage));
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

        // ── SSE 事件流（W8 进度推送）──
        routes.MapGet("/analyze/async/{jobId}/events", async (string jobId, HttpContext http) =>
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
                    var snap = AnalysisJobRegistry.Instance.Get(jobId);
                    if (snap is null)
                    {
                        await WriteEventAsync("gone", "{\"reason\": \"job not found or swept\"}", ct);
                        return;
                    }

                    bool terminal = snap.Status != AnalysisStatus.Running;
                    await WriteEventAsync(terminal ? "complete" : "progress", SnapJson(snap), ct);
                    if (terminal)
                        return;

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
        text/event-stream 事件流（W8 进度推送）：
        - event: progress  — data 为 {processedObjects, status}（每 500ms 一次，值变化也照发，前端可自行节流）；
        - event: complete  — 终态事件，data 为 {processedObjects, status, error}；收到后应关闭 EventSource 并 GET /{jobId} 拉取结果；
        - event: gone      — 作业不存在或已被清理（超过 5 分钟保留窗口）。
        客户端断开时服务端自动结束流。结果本体不随事件推送（可能为数 MB），统一走状态查询端点。
        """);

        // ── 取消作业 ──
        routes.MapPost("/analyze/async/{jobId}/cancel",
            Results<Ok<AnalyzeCancelResponse>, NotFound<ErrorResponse>> (string jobId) =>
        {
            bool ok = AnalysisJobRegistry.Instance.Cancel(jobId);
            var snap = AnalysisJobRegistry.Instance.Get(jobId);
            if (snap is null)
                return TypedResults.NotFound(new ErrorResponse($"作业不存在或已清理：{jobId}"));
            return TypedResults.Ok(new AnalyzeCancelResponse(jobId, ok));
        })
        .WithTags("异步分析")
        .WithSummary("取消异步分析作业")
        .WithDescription("触发对应作业的 CancellationTokenSource。返回 cancelled=true 表示已成功触发；作业不存在 / 已终态时返回 cancelled=false（404 仅在不存在时返回）。");

        return routes;
    }

    // ── 后台执行入口：把异常 / OperationCanceledException 写回 AnalysisJobRegistry ──
    // 临时文件由本方法独占：执行结束（任何原因）后自行清理，避免 finally 在任务读取前把文件删掉。
    private static void RunJob(
        string jobId,
        string dataPath,
        string? rulesPath,
        AnalysisRules? rules,
        AnalysisOptions options,
        CancellationToken token)
    {
        try
        {
            // W8：管线进度经 progressBridge 桥接到注册中心（GET /{jobId} 的 ProcessedObjects
            // 与 SSE 事件流消费）；预览样本随 Complete 一并写回。
            var progressBridge = new Progress<long>(
                count => AnalysisJobRegistry.Instance.ReportProgress(jobId, count));

            var outcome = AnalysisCore.AnalyzeFile(dataPath, rules, options, token, progressBridge);
            AnalysisJobRegistry.Instance.Complete(jobId, outcome.Result, outcome.Preview);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // AnalysisJobRegistry 已经在 Cancel() 调用时把 Status 改成 Cancelled，
            // 这里不需要再写状态。但仍需确认终态（防止 cts.Cancel 触发回调时机晚于 AnalysisRunner 抛异常）。
            var snap = AnalysisJobRegistry.Instance.Get(jobId);
            if (snap is { Status: AnalysisStatus.Running })
            {
                // 触发顺序异常：AnalysisRunner 抛了 Cancel，但 registry 还没标终态
                // 此时直接复用 Fail 包一层，避免状态卡在 Running
            }
        }
        catch (Exception ex)
        {
            // 调试期输出完整堆栈到服务器控制台；正式环境会因 stdout 关闭而沉默
            Console.Error.WriteLine($"[async] job={jobId} failed: {ex}");
            AnalysisJobRegistry.Instance.Fail(jobId, ex);
        }
        finally
        {
            // 不管成功/失败/取消都把临时文件清掉
            TryDelete(dataPath);
            if (rulesPath is not null) TryDelete(rulesPath);
        }
    }

    /// <summary>静默删除一个临时文件（失败仅吞异常）。</summary>
    private static void TryDelete(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 文件可能已被释放 */ }
    }
}
