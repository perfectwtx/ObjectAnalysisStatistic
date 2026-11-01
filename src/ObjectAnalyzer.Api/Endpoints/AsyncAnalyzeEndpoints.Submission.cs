using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using ObjectAnalyzer.Analysis;
using ObjectAnalyzer.Parsing;
using ObjectAnalyzer.Platform;

namespace ObjectAnalyzer.Api;

public static partial class AsyncAnalyzeEndpoints
{
    private static void MapSubmissionEndpoints(IEndpointRouteBuilder routes, ILogger jobLogger)
    {
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
            var backgroundOwnsResources = false;
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

                backgroundOwnsResources = true;

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

                return TypedResults.BadRequest(ApiErrors.FromException(ex));
            }
            finally
            {
                if (!backgroundOwnsResources)
                {
                    TryDelete(tempData);
                    TryDelete(tempRules);
                    cts.Dispose();
                }
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
            var backgroundOwnsResources = false;
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

                backgroundOwnsResources = true;

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
                if (job is not null)
                {
                    try
                    {
                        await jobService.FailAsync(job, ex, CancellationToken.None);
                    }
                    catch { }

                    AnalysisJobRegistry.Instance.Fail(job.Id.ToString("N"), ex);
                }

                return TypedResults.BadRequest(ApiErrors.FromException(ex));
            }
            finally
            {
                if (!backgroundOwnsResources)
                {
                    TryDelete(tempData);
                    cts.Dispose();
                }
            }
        })
        .WithTags("异步分析")
        .WithSummary("异步分析（强类型 JSON 请求体）")
        .Accepts<AnalyzeConfigRequest>("application/json")
        .DisableAntiforgery();
    }
}
