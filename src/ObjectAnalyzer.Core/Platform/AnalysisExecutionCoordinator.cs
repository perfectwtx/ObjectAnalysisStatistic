using ObjectAnalyzer.Analysis;
using ObjectAnalyzer.Analysis.Gates;

namespace ObjectAnalyzer.Platform;

/// <summary>统一封装一次分析执行的 Job 生命周期，避免 UI、API 等调用方重复处理平台状态。</summary>
public sealed class AnalysisExecutionCoordinator
{
    private readonly AnalysisJobService _jobService;

    public AnalysisExecutionCoordinator(AnalysisJobService jobService)
    {
        _jobService = jobService;
    }

    public async Task<AnalysisResult> ExecuteAsync(
        string sourceName,
        string sourceType,
        Func<CancellationToken, Task<AnalysisResult>> execute,
        AnalysisOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(execute);

        var job = await _jobService.StartAsync(sourceName, sourceType, cancellationToken);
        try
        {
            var result = await execute(cancellationToken);
            await _jobService.CompleteAsync(job, result, options, CancellationToken.None);
            return result;
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
