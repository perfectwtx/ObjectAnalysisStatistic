using System.Text.Json;
using ObjectAnalyzer.Analysis;

namespace ObjectAnalyzer.Platform;

/// <summary>负责分析作业生命周期、质量评分、历史归档与质量告警入队。</summary>
public sealed class AnalysisJobService
{
    private readonly IAnalysisHistoryStore _historyStore;
    private readonly IAlertRuleStore _alertRuleStore;
    private readonly IWebhookDeliveryStore _webhookDeliveryStore;

    public AnalysisJobService(IAnalysisHistoryStore historyStore, IAlertRuleStore alertRuleStore, IWebhookDeliveryStore webhookDeliveryStore)
    {
        _historyStore = historyStore;
        _alertRuleStore = alertRuleStore;
        _webhookDeliveryStore = webhookDeliveryStore;
    }

    public async Task<AnalysisJob> StartAsync(string sourceName, string sourceType, CancellationToken cancellationToken = default)
    {
        var job = new AnalysisJob { SourceName = sourceName, SourceType = sourceType, Status = AnalysisJobStatus.Running, StartedAtUtc = DateTime.UtcNow };
        await _historyStore.SaveAsync(job, cancellationToken);
        return job;
    }

    public async Task CompleteAsync(AnalysisJob job, AnalysisResult result, CancellationToken cancellationToken = default)
    {
        var previousJob = (await _historyStore.GetRecentAsync(100, cancellationToken))
            .Where(x => x.Status == AnalysisJobStatus.Succeeded
                     && string.Equals(x.SourceName, job.SourceName, StringComparison.OrdinalIgnoreCase)
                     && x.Id != job.Id)
            .OrderByDescending(x => x.CompletedAtUtc ?? x.StartedAtUtc)
            .FirstOrDefault();

        job.Status = AnalysisJobStatus.Succeeded;
        job.CompletedAtUtc = DateTime.UtcNow;
        job.DurationMs = Math.Max(0, (long)(job.CompletedAtUtc.Value - job.StartedAtUtc).TotalMilliseconds);
        job.TotalObjects = result.TotalObjects;
        job.FieldCount = result.FieldStatistics.Count;
        job.QualityScore = QualityScoreCalculator.Calculate(result);
        job.IssueCount = result.QualityViolations.Count + result.TotalDuplicateGroups;
        job.ResultJson = JsonSerializer.Serialize(result);

        await _historyStore.SaveAsync(job, cancellationToken);
        await EnqueueAlertsAsync(job, result, previousJob, cancellationToken);
    }

    public async Task FailAsync(AnalysisJob job, Exception exception, CancellationToken cancellationToken = default)
    {
        job.Status = AnalysisJobStatus.Failed;
        job.CompletedAtUtc = DateTime.UtcNow;
        job.DurationMs = Math.Max(0, (long)(job.CompletedAtUtc.Value - job.StartedAtUtc).TotalMilliseconds);
        job.ErrorMessage = exception.Message;
        await _historyStore.SaveAsync(job, cancellationToken);
    }

    public async Task CancelAsync(AnalysisJob job, CancellationToken cancellationToken = default)
    {
        job.Status = AnalysisJobStatus.Cancelled;
        job.CompletedAtUtc = DateTime.UtcNow;
        job.DurationMs = Math.Max(0, (long)(job.CompletedAtUtc.Value - job.StartedAtUtc).TotalMilliseconds);
        job.ErrorMessage = "分析已取消。";
        await _historyStore.SaveAsync(job, cancellationToken);
    }

    private async Task EnqueueAlertsAsync(AnalysisJob job, AnalysisResult result, AnalysisJob? previousJob, CancellationToken cancellationToken)
    {
        if (!job.QualityScore.HasValue) return;

        var previousScore = previousJob?.QualityScore;
        var scoreDelta = previousScore.HasValue ? job.QualityScore.Value - previousScore.Value : (double?)null;
        var rules = await _alertRuleStore.GetAllAsync(cancellationToken);

        foreach (var rule in rules)
        {
            if (!AlertEvaluator.ShouldNotify(rule, result, job.QualityScore.Value, scoreDelta)) continue;
            if (string.IsNullOrWhiteSpace(rule.WebhookUrl)) continue;

            var webhookEvent = new WebhookEvent
            {
                EventType = scoreDelta is <= -0.01 ? "quality.alert.regression" : "quality.alert",
                OccurredAtUtc = DateTime.UtcNow,
                JobId = job.Id,
                Source = job.SourceName,
                PreviousScore = previousScore,
                CurrentScore = job.QualityScore,
                IssueCount = job.IssueCount
            };

            await _webhookDeliveryStore.EnqueueAsync(new WebhookDelivery
            {
                RuleId = rule.Id,
                JobId = job.Id,
                EventType = webhookEvent.EventType,
                WebhookUrl = rule.WebhookUrl,
                PayloadJson = JsonSerializer.Serialize(webhookEvent),
                Status = WebhookDeliveryStatus.Pending,
                NextAttemptAtUtc = DateTime.UtcNow
            }, cancellationToken);
        }
    }
}
