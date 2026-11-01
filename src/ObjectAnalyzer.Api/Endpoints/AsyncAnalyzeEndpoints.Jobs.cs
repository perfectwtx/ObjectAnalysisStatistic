using ObjectAnalyzer.Analysis;
using ObjectAnalyzer.Parsing;
using ObjectAnalyzer.Platform;

namespace ObjectAnalyzer.Api;

public static partial class AsyncAnalyzeEndpoints
{
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
                token => AnalysisCore.AnalyzeFileAsync(dataPath, rules, options, token, progressBridge),
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
