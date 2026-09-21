using System.Text.Json;
using ObjectAnalyzer.Analysis;
using ObjectAnalyzer.Platform;

namespace ObjectAnalyzer.Desktop.Services;

/// <summary>Desktop 本地工作台的历史、质量基线与趋势服务。</summary>
public sealed class DesktopHistoryService
{
    private readonly string _databasePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ObjectAnalyzer",
        "object-analyzer.db");

    private SqliteAnalysisHistoryStore CreateHistoryStore() => new(_databasePath);
    private SqliteQualityHistoryQuery CreateQualityQuery() => new(_databasePath);
    private SqliteQualityBaselineStore CreateBaselineStore() => new(_databasePath);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await CreateHistoryStore().InitializeAsync(cancellationToken);
        await CreateBaselineStore().InitializeAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AnalysisJob>> GetRecentJobsAsync(int limit = 100, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        return await CreateHistoryStore().GetRecentAsync(limit, cancellationToken);
    }

    public async Task<IReadOnlyList<QualitySnapshot>> GetTrendAsync(string? sourceName = null, int limit = 100, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        return await CreateQualityQuery().GetSnapshotsAsync(sourceName, limit, cancellationToken);
    }

    public async Task<QualityBaseline?> GetBaselineAsync(string sourceName, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        return await CreateBaselineStore().GetAsync(sourceName, cancellationToken);
    }

    public async Task SaveBaselineAsync(QualityBaseline baseline, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await CreateBaselineStore().SaveAsync(baseline, cancellationToken);
    }

    public static QualityBaselineEvaluation Evaluate(QualityBaseline baseline, AnalysisResult result)
        => QualityBaselineEvaluator.Evaluate(baseline, QualityMetricsCalculator.Calculate(result));

    public async Task<AnalysisJob?> GetJobAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        return await CreateHistoryStore().GetAsync(id, cancellationToken);
    }

    public static AnalysisResult? DeserializeResult(AnalysisJob job)
    {
        if (string.IsNullOrWhiteSpace(job.ResultJson))
            return null;

        try
        {
            return JsonSerializer.Deserialize<AnalysisResult>(job.ResultJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task SaveSuccessfulJobAsync(
        AnalysisJob job,
        AnalysisResult result,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        var metrics = QualityMetricsCalculator.Calculate(result);
        job.TotalObjects = result.TotalObjects;
        job.FieldCount = result.FieldStatistics.Count;
        job.QualityScore = metrics.OverallScore;
        job.IssueCount = result.QualityViolations.Count;
        job.ResultJson = JsonSerializer.Serialize(result);
        job.CompletedAtUtc ??= DateTime.UtcNow;

        var history = CreateHistoryStore();
        await history.SaveAsync(job, cancellationToken);

        var fields = result.FieldStatistics.Select(field =>
            new FieldQualitySnapshot(
                job.Id,
                field.FieldName,
                field.Coverage,
                result.TotalObjects <= 0 ? 0 : field.NullCount * 100d / result.TotalObjects,
                field.DistinctCount is null || result.TotalObjects <= 0
                    ? null
                    : field.DistinctCount.Value * 100d / result.TotalObjects,
                field.OutlierRatio)).ToArray();

        var issues = result.QualityViolations.Select(issue =>
            new QualityIssue(job.Id, issue.Field, issue.Check, issue.Count, issue.Message)).ToArray();

        var snapshot = new QualitySnapshot(
            job.Id,
            job.SourceName,
            job.CompletedAtUtc.Value,
            job.TotalObjects,
            job.FieldCount,
            job.IssueCount,
            metrics);

        await history.SaveQualitySnapshotAsync(snapshot, fields, issues, cancellationToken);
    }
}
