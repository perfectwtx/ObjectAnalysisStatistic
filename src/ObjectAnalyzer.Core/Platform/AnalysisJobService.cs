using System.Text.Json;
using ObjectAnalyzer.Analysis;

namespace ObjectAnalyzer.Platform;

/// <summary>负责分析作业生命周期、质量评分与历史归档。</summary>
public sealed class AnalysisJobService
{
    private readonly IAnalysisHistoryStore _historyStore;

    public AnalysisJobService(IAnalysisHistoryStore historyStore)
    {
        _historyStore = historyStore;
    }

    public async Task<AnalysisJob> StartAsync(
        string sourceName,
        string sourceType,
        CancellationToken cancellationToken = default)
    {
        var job = new AnalysisJob
        {
            SourceName = sourceName,
            SourceType = sourceType,
            Status = AnalysisJobStatus.Running,
            StartedAtUtc = DateTime.UtcNow
        };

        await _historyStore.SaveAsync(job, cancellationToken);
        return job;
    }

    public async Task CompleteAsync(
        AnalysisJob job,
        AnalysisResult result,
        CancellationToken cancellationToken = default)
    {
        job.Status = AnalysisJobStatus.Succeeded;
        job.CompletedAtUtc = DateTime.UtcNow;
        job.DurationMs = Math.Max(0, (long)(job.CompletedAtUtc.Value - job.StartedAtUtc).TotalMilliseconds);
        job.TotalObjects = result.TotalObjects;
        job.FieldCount = result.FieldStatistics.Count;
        job.QualityScore = QualityScoreCalculator.Calculate(result);
        job.IssueCount = result.QualityViolations.Count
                         + result.TotalDuplicateGroups;
        job.ResultJson = JsonSerializer.Serialize(result);

        await _historyStore.SaveAsync(job, cancellationToken);
    }

    public async Task FailAsync(
        AnalysisJob job,
        Exception exception,
        CancellationToken cancellationToken = default)
    {
        job.Status = AnalysisJobStatus.Failed;
        job.CompletedAtUtc = DateTime.UtcNow;
        job.DurationMs = Math.Max(0, (long)(job.CompletedAtUtc.Value - job.StartedAtUtc).TotalMilliseconds);
        job.ErrorMessage = exception.Message;

        await _historyStore.SaveAsync(job, cancellationToken);
    }
}