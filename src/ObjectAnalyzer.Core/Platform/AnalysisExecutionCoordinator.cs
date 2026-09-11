using ObjectAnalyzer.Analysis;

namespace ObjectAnalyzer.Platform;

/// <summary>统一封装一次分析执行的 Job 生命周期，避免 UI、API 等调用方重复处理平台状态。</summary>
public sealed class AnalysisExecutionCoordinator
{
    private readonly AnalysisJobService _jobService;

    public AnalysisExecutionCoordinator(AnalysisJobService jobService)
    {
        _jobService = jobService;
    }

    public Task<AnalysisResult> ExecuteAsync(
        string sourceName,
        string sourceType,
        Func<CancellationToken, Task<AnalysisResult>> execute,
        AnalysisOptions? options = null,
        CancellationToken cancellationToken = default)
        => ExecuteAsync(
            sourceName,
            sourceType,
            execute,
            static result => result,
            options,
            cancellationToken);

    /// <summary>
    /// 执行一次分析并统一处理 Job 生命周期。
    /// <para>
    /// 返回值可以是宿主需要的富结果，同时通过 <paramref name="resultSelector"/>
    /// 指定用于历史、质量评分、门禁和告警的 AnalysisResult。
    /// </para>
    /// </summary>
    public async Task<T> ExecuteAsync<T>(
        string sourceName,
        string sourceType,
        Func<CancellationToken, Task<T>> execute,
        Func<T, AnalysisResult> resultSelector,
        AnalysisOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(execute);
        ArgumentNullException.ThrowIfNull(resultSelector);

        var job = await _jobService.StartAsync(sourceName, sourceType, cancellationToken);
        return await ExecuteAsync(job, execute, resultSelector, options, cancellationToken);
    }

    /// <summary>
    /// 使用调用方已经创建的 Job 执行分析。
    /// <para>
    /// 主要用于异步提交场景：HTTP 请求先创建并返回 Job ID，后台任务随后复用同一个持久化 Job，
    /// 避免重新创建第二个 Job。
    /// </para>
    /// </summary>
    public async Task<T> ExecuteAsync<T>(
        AnalysisJob job,
        Func<CancellationToken, Task<T>> execute,
        Func<T, AnalysisResult> resultSelector,
        AnalysisOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(execute);
        ArgumentNullException.ThrowIfNull(resultSelector);

        if (job.Status != AnalysisJobStatus.Running)
            throw new InvalidOperationException($"分析作业 {job.Id} 当前状态为 {job.Status}，不能执行。 ");

        try
        {
            var value = await execute(cancellationToken);
            var result = resultSelector(value);
            await _jobService.CompleteAsync(job, result, options, CancellationToken.None);
            return value;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await _jobService.CancelAsync(job, CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            await _jobService.FailAsync(job, ex, CancellationToken.None);
            throw;
        }
    }
}
