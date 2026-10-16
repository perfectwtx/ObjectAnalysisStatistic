using System.ComponentModel;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using System.Text.Json;
using ObjectAnalyzer.Analysis;
using ObjectAnalyzer.Parsing;
using ObjectAnalyzer.Platform;

namespace ObjectAnalyzer.Api;

/// <summary>
/// 异步分析端点：
/// - POST /api/analyze/async                   提交分析作业，返回 jobId
/// - GET  /api/analyze/async/{jobId}           查询作业状态 / 拉取结果
/// - GET  /api/analyze/async/{jobId}/events   订阅进度事件
/// - POST /api/analyze/async/{jobId}/cancel    取消正在进行的分析
///
/// 持久化 AnalysisJob 是平台作业的唯一标识；AnalysisJobRegistry 只负责进程内的
/// CancellationToken、实时进度、SSE 和短期结果缓存。后台任务通过独立 DI Scope
/// 复用已经创建的 AnalysisJob，避免 HTTP 请求结束后继续使用已释放的请求 Scope。
///
/// 数据库 / 非文件型输入场景不支持取消；这些请求一律走同步端点 /api/analyze。
/// </summary>
public static class AsyncAnalyzeEndpoints
{
    private static readonly JsonSerializerOptions s_sseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly Action<ILogger, string, Exception?> s_jobFailed = LoggerMessage.Define<string>(
        LogLevel.Error, new EventId(1, "JobFailed"), "异步分析作业 {JobId} 失败");

    public static IEndpointRouteBuilder MapAsyncAnalyzeApi(this IEndpointRouteBuilder routes)
    {
        var jobLogger = routes.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger("ObjectAnalyzer.AsyncAnalyze");

        // ── 异步分析：multipart 上传 ──
        routes.MapPost("/analyze/async",
            async Task<Results<Ok<AnalyzeAsyncResponse>, BadRequest<ErrorResponse>>> (
            [FromForm] AnalyzeForm form,
            HttpRequest request,
            AnalysisJobService jobService,
            IServiceScopeFactory scopeFactory,
            CancellationToken cancellationToken) =>
        {
            var file = form.File;
            if (file is null || file.Length == 0)
                return TypedResults.BadRequest(new ErrorResponse("请通过 multipart 表单上传数据文件（字段名 file）。"));

            var tempData = Path.Combine(Path.GetTempPath(), $"oa-async-{Guid.NewGuid():N}{Path.GetExtension(file.FileName)}");
            var tempRules = Path.Combine(Path.GetTempPath(), $"oa-async-{Guid.NewGuid():N}.json");
            AnalysisJob? job = null;
            var cts = new CancellationTokenSource();
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

                // 持久化 Job 先创建，返回给客户端的 ID 与后台运行时 Registry ID 完全一致。
                job = await jobService.StartAsync(
                    sourceName: file.FileName,
                    sourceType: effectiveFormat,
                    cancellationToken);

                AnalysisJobRegistry.Instance.Register(
                    job.Id.ToString("N"),
                    cts,
                    new AnalysisJobMeta(
                        DataFileName: file.FileName,
                        Format: effectiveFormat,
                        OperatorId: null,
                        SubmittedAt: DateTimeOffset.UtcNow));

                var jobForTask = job;
                var jobIdForTask = job.Id.ToString("N");
                var dataPathForTask = tempData;
                var rulesPathForTask = tempRules;
                var rulesForTask = rulesObj;
                var optionsForTask = options;

                _ = Task.Run(
                    () => RunJob(
                        jobLogger,
                        scopeFactory,
                        jobForTask,
                        jobIdForTask,
                        dataPathForTask,
                        rulesPathForTask,
                        rulesForTask,
                        optionsForTask,
                        cts),
                    CancellationToken.None);

                tempData = null!;
                tempRules = null!;

                return TypedResults.Ok(new AnalyzeAsyncResponse(
                    JobId: jobIdForTask,
                    Status: AnalysisStatus.Running,
                    FileName: file.FileName,
                    Format: effectiveFormat,
                    RulesSource: rulesSource,
                    SubmittedAt: DateTimeOffset.UtcNow));
            }
            catch (Exception ex)
            {
                try { cts.Cancel(); } catch { }
                try { cts.Dispose(); } catch { }

                if (job is not null)
                {
                    // StartAsync 已成功时，必须把持久化 Job 也置为 Failed，避免留下永久 Running。
                    try
                    {
                        await jobService.FailAsync(job, ex, CancellationToken.None);
                    }
                    catch { }

                    AnalysisJobRegistry.Instance.Fail(job.Id.ToString("N"), ex);
                }

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
        - GET  /api/analyze/async/{jobId}/events   订阅进度
        - POST /api/analyze/async/{jobId}/cancel    触发取消
        持久化 AnalysisJob 是作业的唯一 ID；运行时 Registry 只保存进程内状态。
        """)
        .DisableAntiforgery();

        // ── 异步分析：强类型 JSON body ──
        routes.MapPost("/analyze/async/config",
            async Task<Results<Ok<AnalyzeAsyncResponse>, BadRequest<ErrorResponse>>> (
            HttpRequest request,
            AnalysisJobService jobService,
            IServiceScopeFactory scopeFactory,
            CancellationToken cancellationToken) =>
        {
            var (config, configError) = await AnalysisCore.ReadConfigRequestAsync(request, cancellationToken);
            if (configError is not null)
                return TypedResults.BadRequest(configError);

            if (string.IsNullOrWhiteSpace(config!.DataText))
                return TypedResults.BadRequest(new ErrorResponse("缺少数据文本：请提供 dataText。"));

            var tempData = Path.Combine(Path.GetTempPath(), $"oa-async-{Guid.NewGuid():N}.txt");
            AnalysisJob? job = null;
            var cts = new CancellationTokenSource();
            try
            {
                await File.WriteAllTextAsync(tempData, config.DataText, cancellationToken);

                var effectiveFormat = AnalysisCore.ResolveEffectiveFormat(tempData, config.Rules, config.Format);
                var options = config.ToAnalysisOptions(tempData, config.Rules);
                options.Input.Format = config.Format;

                job = await jobService.StartAsync(
                    sourceName: "(inline)",
                    sourceType: effectiveFormat,
                    cancellationToken);

                var jobId = job.Id.ToString("N");
                AnalysisJobRegistry.Instance.Register(
                    jobId,
                    cts,
                    new AnalysisJobMeta(
                        DataFileName: "(inline)",
                        Format: effectiveFormat,
                        OperatorId: null,
                        SubmittedAt: DateTimeOffset.UtcNow));

                var jobForTask = job;
                var jobIdForTask = jobId;
                var dataPathForTask = tempData;
                var rulesForTask = config.Rules;
                var optionsForTask = options;

                _ = Task.Run(
                    () => RunJob(
                        jobLogger,
                        scopeFactory,
                        jobForTask,
                        jobIdForTask,
                        dataPathForTask,
                        null,
                        rulesForTask,
                        optionsForTask,
                        cts),
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

                if (job is not null)
                {
                    try
                    {
                        await jobService.FailAsync(job, ex, CancellationToken.None);
                    }
                    catch { }

                    AnalysisJobRegistry.Instance.Fail(job.Id.ToString("N"), ex);
                }

                TryDelete(tempData);
                return TypedResults.BadRequest(ApiErrors.FromException(ex));
            }
        })
        .WithTags("异步分析")
        .WithSummary("异步分析（强类型 JSON 请求体）")
        .Accepts<AnalyzeConfigRequest>("application/json")
        .DisableAntiforgery();

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

            // Registry 只保留进程内的短期运行时状态。终态作业被清理后，
            // 客户端仍应能通过持久化历史查询到结果。应用重启后同样走这里。
            var snap = AnalysisJobRegistry.Instance.Get(jobId);
            if (snap is not null)
                return TypedResults.Ok(ToResponse(snap));

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
                    // Use the same terminal authority as polling: persistence may finish
                    // before the background task publishes its terminal Registry snapshot.
                    if (Guid.TryParse(jobId, out var terminalJobId))
                    {
                        var terminalJob = await historyStore.GetAsync(terminalJobId, ct);
                        if (terminalJob is not null && terminalJob.Status is
                            (AnalysisJobStatus.Succeeded or AnalysisJobStatus.Failed or AnalysisJobStatus.Cancelled))
                        {
                            var response = ToResponse(terminalJob);
                            await WriteEventAsync("complete", JsonSerializer.Serialize(new
                            {
                                processedObjects = response.ProcessedObjects,
                                status = response.Status.ToString(),
                                error = response.Error
                            }, s_sseJsonOptions), ct);
                            return;
                        }
                    }

                    var snap = AnalysisJobRegistry.Instance.Get(jobId);
                    if (snap is not null)
                    {
                        bool terminal = snap.Status != AnalysisStatus.Running;
                        await WriteEventAsync(terminal ? "complete" : "progress", SnapJson(snap), ct);
                        if (terminal)
                            return;
                    }
                    else if (Guid.TryParse(jobId, out var persistedJobId))
                    {
                        var persisted = await historyStore.GetAsync(persistedJobId, ct);
                        if (persisted is null)
                        {
                            await WriteEventAsync("gone", "{\"reason\":\"job not found\"}", ct);
                            return;
                        }

                        var terminal = persisted.Status is not (AnalysisJobStatus.Pending or AnalysisJobStatus.Running);
                        var persistedStatus = persisted.Status switch
                        {
                            AnalysisJobStatus.Succeeded => "Completed",
                            AnalysisJobStatus.Failed => "Failed",
                            AnalysisJobStatus.Cancelled => "Cancelled",
                            _ => "Running"
                        };
                        await WriteEventAsync(terminal ? "complete" : "progress",
                            JsonSerializer.Serialize(new { processedObjects = persisted.TotalObjects, status = persistedStatus, error = persisted.ErrorMessage }, s_sseJsonOptions), ct);
                        if (terminal)
                            return;
                    }
                    else
                    {
                        await WriteEventAsync("gone", "{\"reason\":\"invalid job id\"}", ct);
                        return;
                    }

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

            var runtimeSnap = AnalysisJobRegistry.Instance.Get(jobId);
            if (runtimeSnap is not null)
                return TypedResults.Ok(new AnalyzeCancelResponse(jobId, AnalysisJobRegistry.Instance.Cancel(jobId)));

            return TypedResults.NotFound(new ErrorResponse($"作业不存在或已清理：{jobId}"));
        })
        .WithTags("异步分析")
        .WithSummary("取消异步分析作业")
        .WithDescription("触发对应作业的 CancellationTokenSource。运行时作业存在时返回 cancelled=true/false；运行时状态已清理但持久化作业已终态时返回 cancelled=false；持久化作业仍在运行但缺少运行时句柄时返回 409。");

        return routes;
    }

        private static DateTimeOffset ToUtcOffset(DateTime value)
        => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static AnalyzeJobSnapshotResponse ToResponse(AnalysisJobSnapshot snap)
    {
        string? errorMessage = snap.Error is { } err
            ? $"{err.GetType().Name}: {err.Message}"
            : null;

        return new AnalyzeJobSnapshotResponse(
            Id: snap.Id,
            Status: snap.Status,
            Meta: snap.Meta,
            CreatedAt: snap.CreatedAt,
            TerminalAt: snap.TerminalAt,
            ProgressText: snap.ProgressText,
            ProcessedObjects: snap.ProcessedObjects,
            Result: snap.Result,
            Preview: snap.Preview,
            Error: errorMessage,
            PiiRedacted: snap.PiiRedacted,
            Pii: AnalyzePiiView.From(snap.Pii));
    }

    private static AnalyzeJobSnapshotResponse ToResponse(AnalysisJob job, AnalysisJobSnapshot? runtime = null)
    {
        var status = job.Status switch
        {
            AnalysisJobStatus.Succeeded => AnalysisStatus.Completed,
            AnalysisJobStatus.Failed => AnalysisStatus.Failed,
            AnalysisJobStatus.Cancelled => AnalysisStatus.Cancelled,
            _ => AnalysisStatus.Running
        };

        AnalysisResult? result = null;
        if (status == AnalysisStatus.Completed && !string.IsNullOrWhiteSpace(job.ResultJson))
        {
            try
            {
                result = JsonSerializer.Deserialize<AnalysisResult>(job.ResultJson);
            }
            catch (JsonException)
            {
                // 历史结果损坏时仍返回作业状态和其它元数据，不让查询端点整体失败。
            }
        }

        return new AnalyzeJobSnapshotResponse(
            Id: job.Id.ToString("N"),
            Status: status,
            Meta: new AnalysisJobMeta(
                DataFileName: job.SourceName,
                Format: job.SourceType,
                OperatorId: null,
                SubmittedAt: ToUtcOffset(job.StartedAtUtc)),
            CreatedAt: ToUtcOffset(job.StartedAtUtc),
            TerminalAt: job.CompletedAtUtc.HasValue
                ? ToUtcOffset(job.CompletedAtUtc.Value)
                : null,
            ProgressText: null,
            ProcessedObjects: job.TotalObjects,
            Result: result,
            Preview: status == AnalysisStatus.Completed ? runtime?.Preview : null,
            Error: job.Status == AnalysisJobStatus.Failed ? job.ErrorMessage : null,
            PiiRedacted: status == AnalysisStatus.Completed ? runtime?.PiiRedacted ?? true : true,
            Pii: status == AnalysisStatus.Completed ? AnalyzePiiView.From(runtime?.Pii) : null);
    }

    private static async Task RunJob(
        ILogger jobLogger,
        IServiceScopeFactory scopeFactory,
        AnalysisJob job,
        string jobId,
        string dataPath,
        string? rulesPath,
        AnalysisRules? rules,
        AnalysisOptions options,
        CancellationTokenSource jobCts)
    {
        try
        {
            // HTTP 请求已经结束，此处必须创建独立 Scope，不能捕获请求 Scope 中的 scoped 服务。
            await using var scope = scopeFactory.CreateAsyncScope();
            var coordinator = scope.ServiceProvider.GetRequiredService<AnalysisExecutionCoordinator>();

            var progressBridge = new Progress<long>(
                count => AnalysisJobRegistry.Instance.ReportProgress(jobId, count));

            var outcome = await coordinator.ExecuteAsync(
                job,
                token => Task.Run(
                    () => AnalysisCore.AnalyzeFile(dataPath, rules, options, token, progressBridge),
                    token),
                value =>
                {
                    // Publish delivery metadata before CompleteAsync commits the terminal state.
                    // Status and Result remain authoritative in the persistent history.
                    AnalysisJobRegistry.Instance.StageOutput(jobId, value.Preview, value.PiiRedacted, value.Pii);
                    return value.Result;
                },
                options,
                jobCts.Token);

            AnalysisJobRegistry.Instance.Complete(
                jobId,
                outcome.Result,
                outcome.Preview,
                outcome.PiiRedacted,
                outcome.Pii);
        }
        catch (OperationCanceledException) when (jobCts.IsCancellationRequested)
        {
            // Coordinator 已将持久化 Job 置为 Cancelled；Registry.Cancel 已将运行时 Job 置为 Cancelled。
            // 如果取消发生在 Registry.Cancel 之前，则补齐运行时终态。
            var snap = AnalysisJobRegistry.Instance.Get(jobId);
            if (snap is { Status: AnalysisStatus.Running })
                AnalysisJobRegistry.Instance.Cancel(jobId);
        }
        catch (Exception ex)
        {
            s_jobFailed(jobLogger, jobId, ex);

            // Coordinator 正常路径已经负责持久化 Failed。这里仅兜底 Registry 状态，
            // 防止在进入 Coordinator 前就发生异常导致运行时作业卡在 Running。
            var snap = AnalysisJobRegistry.Instance.Get(jobId);
            if (snap is { Status: AnalysisStatus.Running })
                AnalysisJobRegistry.Instance.Fail(jobId, ex);
        }
        finally
        {
            TryDelete(dataPath);
            if (rulesPath is not null) TryDelete(rulesPath);
            jobCts.Dispose();
        }
    }

    private static void TryDelete(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
