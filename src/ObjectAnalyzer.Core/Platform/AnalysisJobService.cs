using System.Globalization;
using System.Text.Json;
using ObjectAnalyzer.Analysis;
using ObjectAnalyzer.Analysis.Gates;

namespace ObjectAnalyzer.Platform;

/// <summary>负责分析作业生命周期、质量评分、质量基线、历史归档与质量告警入队。</summary>
public sealed class AnalysisJobService
{
    private readonly IAnalysisHistoryStore _historyStore;
    private readonly IAlertRuleStore _alertRuleStore;
    private readonly IWebhookDeliveryStore _webhookDeliveryStore;
    private readonly IQualityBaselineStore? _qualityBaselineStore;
    private readonly QualityIssueRemediationVerifier? _remediationVerifier;

    public AnalysisJobService(
        IAnalysisHistoryStore historyStore,
        IAlertRuleStore alertRuleStore,
        IWebhookDeliveryStore webhookDeliveryStore,
        IQualityBaselineStore? qualityBaselineStore = null,
        QualityIssueRemediationVerifier? remediationVerifier = null)
    {
        _historyStore = historyStore;
        _alertRuleStore = alertRuleStore;
        _webhookDeliveryStore = webhookDeliveryStore;
        _qualityBaselineStore = qualityBaselineStore;
        _remediationVerifier = remediationVerifier;
    }

    public Task<AnalysisJob> StartAsync(string sourceName, string sourceType, CancellationToken cancellationToken = default)
        => StartAsync(Guid.NewGuid(), sourceName, sourceType, cancellationToken);

    /// <summary>使用调用方已分配的 Job ID 创建运行中的持久化作业。</summary>
    public async Task<AnalysisJob> StartAsync(Guid jobId, string sourceName, string sourceType, CancellationToken cancellationToken = default)
    {
        if (jobId == Guid.Empty) throw new ArgumentException("Job ID 不能为空。", nameof(jobId));

        var job = new AnalysisJob { Id = jobId, SourceName = sourceName, SourceType = sourceType, Status = AnalysisJobStatus.Running, StartedAtUtc = DateTime.UtcNow };
        await _historyStore.SaveAsync(job, cancellationToken);
        return job;
    }

    /// <summary>应用启动时将上一次进程遗留的 Pending/Running 作业标记为失败。</summary>
    public async Task<int> RecoverOrphanedJobsAsync(CancellationToken cancellationToken = default)
    {
        var jobs = await _historyStore.GetRecentAsync(10000, cancellationToken);
        var orphaned = jobs.Where(x => x.Status is AnalysisJobStatus.Pending or AnalysisJobStatus.Running).ToList();
        var recovered = 0;
        foreach (var job in orphaned)
        {
            job.Status = AnalysisJobStatus.Failed;
            job.CompletedAtUtc = DateTime.UtcNow;
            job.DurationMs = Math.Max(0, (long)(job.CompletedAtUtc.Value - job.StartedAtUtc).TotalMilliseconds);
            job.ErrorMessage = "应用重启后检测到未完成的分析作业，已标记为失败。";
            if (await _historyStore.TryTransitionAsync(job, AnalysisJobStatus.Pending, cancellationToken) ||
                await _historyStore.TryTransitionAsync(job, AnalysisJobStatus.Running, cancellationToken))
            {
                recovered++;
            }
        }

        return recovered;
    }

    public async Task<bool> CompleteAsync(AnalysisJob job, AnalysisResult result, AnalysisOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (job.Status != AnalysisJobStatus.Running)
            return;

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
        job.IssueCount = result.QualityViolations.Count + checked((int)Math.Min(result.TotalDuplicateGroups, int.MaxValue));
        job.ResultJson = JsonSerializer.Serialize(result);

        if (_qualityBaselineStore is not null)
        {
            var baseline = await _qualityBaselineStore.GetAsync(job.SourceName, cancellationToken);
            if (baseline is not null)
            {
                var baselineEvaluation = QualityBaselineEvaluator.Evaluate(
                    baseline,
                    QualityMetricsCalculator.Calculate(result));
                job.QualityBaselineEvaluationJson = JsonSerializer.Serialize(baselineEvaluation);
            }
        }

        if (options is not null)
        {
            job.GateResultsJson = JsonSerializer.Serialize(
                QualityGateEvaluation.EvaluateAll(result, options));
        }

        var currentIssues = CreateQualityIssues(job.Id, result);
        if (!await _historyStore.TryTransitionAsync(job, AnalysisJobStatus.Running, cancellationToken))
            return;

        await _historyStore.SaveQualitySnapshotAsync(
            CreateQualitySnapshot(job, result),
            CreateFieldSnapshots(job.Id, result),
            currentIssues,
            cancellationToken);

        if (_remediationVerifier is not null)
        {
            await _remediationVerifier.VerifyAsync(job, previousJob, currentIssues, cancellationToken);
        }

        await EnqueueAlertsAsync(job, result, previousJob, cancellationToken);
    }

    private static QualitySnapshot CreateQualitySnapshot(AnalysisJob job, AnalysisResult result)
    {
        var metrics = QualityMetricsCalculator.Calculate(result);
        return new QualitySnapshot(
            job.Id,
            job.SourceName,
            job.CompletedAtUtc ?? DateTime.UtcNow,
            result.TotalObjects,
            result.FieldStatistics.Count,
            job.IssueCount,
            metrics);
    }

    private static FieldQualitySnapshot[] CreateFieldSnapshots(Guid jobId, AnalysisResult result)
    {
        return result.FieldStatistics.Select(field => new FieldQualitySnapshot(
            jobId,
            field.FieldName,
            Math.Clamp(field.Coverage, 0d, 1d),
            result.TotalObjects <= 0 ? 0d : Math.Clamp((double)field.NullCount / result.TotalObjects, 0d, 1d),
            field.DistinctCount.HasValue && field.Count > 0
                ? Math.Clamp((double)field.DistinctCount.Value / field.Count, 0d, 1d)
                : null,
            field.OutlierRatio.HasValue ? Math.Clamp(field.OutlierRatio.Value, 0d, 1d) : null)).ToArray();
    }

    private static List<QualityIssue> CreateQualityIssues(Guid jobId, AnalysisResult result)
    {
        var issues = result.QualityViolations
            .Select(issue => new QualityIssue(jobId, issue.Field, issue.Check, issue.Count, issue.Message))
            .ToList();

        if (result.TotalDuplicateGroups > 0)
        {
            issues.Add(new QualityIssue(
                jobId,
                string.Empty,
                QualityCheckTypes.DuplicateGroups,
                result.TotalDuplicateGroups,
                string.Create(CultureInfo.InvariantCulture, $"发现 {result.TotalDuplicateGroups:N0} 个重复对象组。")));
        }

        return issues;
    }

    public async Task FailAsync(AnalysisJob job, Exception exception, CancellationToken cancellationToken = default)
    {
        if (job.Status != AnalysisJobStatus.Running)
            return;

        job.Status = AnalysisJobStatus.Failed;
        job.CompletedAtUtc = DateTime.UtcNow;
        job.DurationMs = Math.Max(0, (long)(job.CompletedAtUtc.Value - job.StartedAtUtc).TotalMilliseconds);
        job.ErrorMessage = exception.Message;
        await _historyStore.TryTransitionAsync(job, AnalysisJobStatus.Running, cancellationToken);
    }

    public async Task CancelAsync(AnalysisJob job, CancellationToken cancellationToken = default)
    {
        if (job.Status != AnalysisJobStatus.Running)
            return;

        job.Status = AnalysisJobStatus.Cancelled;
        job.CompletedAtUtc = DateTime.UtcNow;
        job.DurationMs = Math.Max(0, (long)(job.CompletedAtUtc.Value - job.StartedAtUtc).TotalMilliseconds);
        job.ErrorMessage = "分析已取消。";
        await _historyStore.TryTransitionAsync(job, AnalysisJobStatus.Running, cancellationToken);
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
