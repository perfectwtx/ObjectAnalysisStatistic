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
    private readonly IQualityBaselineStore? _qualityBaselineStore;
    private readonly QualityIssueRemediationVerifier? _remediationVerifier;
    private readonly IPlatformDiagnostics _diagnostics;

    public AnalysisJobService(
        IAnalysisHistoryStore historyStore,
        IAlertRuleStore alertRuleStore,
        IWebhookDeliveryStore webhookDeliveryStore,
        IQualityBaselineStore? qualityBaselineStore = null,
        QualityIssueRemediationVerifier? remediationVerifier = null,
        IPlatformDiagnostics? diagnostics = null)
    {
        _historyStore = historyStore;
        _alertRuleStore = alertRuleStore;
        ArgumentNullException.ThrowIfNull(webhookDeliveryStore);
        _qualityBaselineStore = qualityBaselineStore;
        _remediationVerifier = remediationVerifier;
        _diagnostics = diagnostics ?? NullPlatformDiagnostics.Instance;
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
    public Task<int> RecoverOrphanedJobsAsync(CancellationToken cancellationToken = default)
        => _historyStore.RecoverOrphanedJobsAsync(DateTime.UtcNow,
            "应用重启后检测到未完成的分析作业，已标记为失败。", cancellationToken);

    public async Task<bool> CompleteAsync(AnalysisJob job, AnalysisResult result, AnalysisOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (job.Status != AnalysisJobStatus.Running)
            return false;

        var previousJob = await _historyStore.GetPreviousSucceededAsync(job.SourceName, job.Id,
            cancellationToken: cancellationToken);

        var completedJob = new AnalysisJob { Id = job.Id };
        CopyState(job, completedJob);
        completedJob.Status = AnalysisJobStatus.Succeeded;
        completedJob.CompletedAtUtc = DateTime.UtcNow;
        completedJob.DurationMs = Math.Max(0, (long)(completedJob.CompletedAtUtc.Value - completedJob.StartedAtUtc).TotalMilliseconds);
        completedJob.TotalObjects = result.TotalObjects;
        completedJob.FieldCount = result.FieldStatistics.Count;
        completedJob.QualityScore = QualityScoreCalculator.Calculate(result);
        completedJob.IssueCount = result.QualityViolations.Count + checked((int)Math.Min(result.TotalDuplicateGroups, int.MaxValue));
        completedJob.ResultJson = JsonSerializer.Serialize(result);

        if (_qualityBaselineStore is not null)
        {
            var baseline = await _qualityBaselineStore.GetAsync(completedJob.SourceName, cancellationToken);
            if (baseline is not null)
            {
                var baselineEvaluation = QualityBaselineEvaluator.Evaluate(
                    baseline,
                    QualityMetricsCalculator.Calculate(result));
                completedJob.QualityBaselineEvaluationJson = JsonSerializer.Serialize(baselineEvaluation);
            }
        }

        if (options is not null)
        {
            completedJob.GateResultsJson = JsonSerializer.Serialize(
                QualityGateEvaluation.EvaluateAll(result, options));
        }

        var currentIssues = CreateQualityIssues(completedJob.Id, result);
        var deliveries = await CreateAlertsAsync(completedJob, result, previousJob, cancellationToken);
        var completion = new AnalysisCompletion(completedJob, CreateQualitySnapshot(completedJob, result),
            CreateFieldSnapshots(completedJob.Id, result), currentIssues, deliveries);
        if (!await _historyStore.TryCompleteAsync(completion, cancellationToken))
        {
            await ResyncStateAsync(job, cancellationToken);
            return false;
        }
        CopyState(completedJob, job);

        if (_remediationVerifier is not null)
        {
            await VerifyRemediationCoreAsync(job, previousJob, currentIssues, cancellationToken);
        }

        return true;
    }

    /// <summary>Retries post-processing without changing the committed analysis status.</summary>
    public async Task<RemediationVerificationOutcome> RetryRemediationVerificationAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var job = await _historyStore.GetAsync(jobId, cancellationToken)
            ?? throw new KeyNotFoundException("Analysis job was not found.");
        if (job.Status != AnalysisJobStatus.Succeeded || string.IsNullOrWhiteSpace(job.ResultJson))
            throw new InvalidOperationException("Only successful jobs with stored results can be verified.");
        var saved = await _historyStore.GetRemediationVerificationAsync(jobId, cancellationToken);
        var previous = saved is not null
            ? (saved.PreviousJobId.HasValue ? await _historyStore.GetAsync(saved.PreviousJobId.Value, cancellationToken) : null)
            : await _historyStore.GetPreviousSucceededAsync(job.SourceName, job.Id, job.StartedAtUtc, cancellationToken);
        if (saved?.PreviousJobId is not null && previous is null)
            throw new InvalidOperationException("The original previous job has been deleted.");
        var result = JsonSerializer.Deserialize<AnalysisResult>(job.ResultJson)
            ?? throw new InvalidOperationException("Stored analysis result is invalid.");
        return await VerifyRemediationCoreAsync(job, previous, CreateQualityIssues(job.Id, result), cancellationToken);
    }

    private async Task<RemediationVerificationOutcome> VerifyRemediationCoreAsync(AnalysisJob job,
        AnalysisJob? previousJob, IReadOnlyList<QualityIssue> issues, CancellationToken cancellationToken)
    {
        RemediationVerificationOutcome outcome;
        try
        {
            if (_remediationVerifier is null)
                throw new InvalidOperationException("Remediation verification is not configured.");
            await _remediationVerifier.VerifyAsync(job, previousJob, issues, cancellationToken);
            outcome = new(job.Id, previousJob?.Id, true, null, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            // This is post-processing: the successful analysis and outbox are already committed.
            _diagnostics.Report($"Remediation verification failed for {job.Id}", ex);
            outcome = new(job.Id, previousJob?.Id, false, "整改验证失败，可以在修复原因后重试。", DateTime.UtcNow);
        }
        try
        {
            await _historyStore.RecordRemediationVerificationAsync(outcome, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _diagnostics.Report($"Verification outcome persistence failed for {job.Id}", ex);
            outcome = outcome with { Succeeded = false, Error = "整改验证状态未能保存，请重试。" };
        }
        return outcome;
    }

    private async Task ResyncStateAsync(AnalysisJob job, CancellationToken cancellationToken)
    {
        var persisted = await _historyStore.GetAsync(job.Id, cancellationToken);
        if (persisted is not null)
            CopyState(persisted, job);
    }

    private static void CopyState(AnalysisJob source, AnalysisJob target)
    {
        target.SourceName = source.SourceName;
        target.SourceType = source.SourceType;
        target.Status = source.Status;
        target.StartedAtUtc = source.StartedAtUtc;
        target.CompletedAtUtc = source.CompletedAtUtc;
        target.DurationMs = source.DurationMs;
        target.TotalObjects = source.TotalObjects;
        target.FieldCount = source.FieldCount;
        target.QualityScore = source.QualityScore;
        target.IssueCount = source.IssueCount;
        target.ErrorMessage = source.ErrorMessage;
        target.ResultJson = source.ResultJson;
        target.GateResultsJson = source.GateResultsJson;
        target.QualityBaselineEvaluationJson = source.QualityBaselineEvaluationJson;
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
        if (!await _historyStore.TryTransitionAsync(job, AnalysisJobStatus.Running, cancellationToken))
            await ResyncStateAsync(job, cancellationToken);
    }

    public async Task CancelAsync(AnalysisJob job, CancellationToken cancellationToken = default)
    {
        if (job.Status != AnalysisJobStatus.Running)
            return;

        job.Status = AnalysisJobStatus.Cancelled;
        job.CompletedAtUtc = DateTime.UtcNow;
        job.DurationMs = Math.Max(0, (long)(job.CompletedAtUtc.Value - job.StartedAtUtc).TotalMilliseconds);
        job.ErrorMessage = "分析已取消。";
        if (!await _historyStore.TryTransitionAsync(job, AnalysisJobStatus.Running, cancellationToken))
            await ResyncStateAsync(job, cancellationToken);
    }

    private async Task<IReadOnlyList<WebhookDelivery>> CreateAlertsAsync(AnalysisJob job, AnalysisResult result, AnalysisJob? previousJob, CancellationToken cancellationToken)
    {
        var deliveries = new List<WebhookDelivery>();
        if (!job.QualityScore.HasValue) return deliveries;

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

            deliveries.Add(new WebhookDelivery
            {
                RuleId = rule.Id,
                JobId = job.Id,
                EventType = webhookEvent.EventType,
                WebhookUrl = rule.WebhookUrl,
                PayloadJson = JsonSerializer.Serialize(webhookEvent),
                Status = WebhookDeliveryStatus.Pending,
                NextAttemptAtUtc = DateTime.UtcNow
            });
        }
        return deliveries;
    }
}
