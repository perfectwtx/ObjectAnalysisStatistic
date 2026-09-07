using ObjectAnalyzer.Analysis;

namespace ObjectAnalyzer.Platform;

/// <summary>比较相邻两次分析结果，识别质量分数、字段指标和质量违规的回归。</summary>
public static class QualityRegressionAnalyzer
{
    public static QualityRegressionReport Analyze(AnalysisResult previous, AnalysisResult current, double? previousScore = null, double? currentScore = null)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);
        var fields = new List<QualityFieldRegression>();
        var previousFields = previous.FieldStatistics.ToDictionary(x => x.FieldName, StringComparer.Ordinal);
        var currentFields = current.FieldStatistics.ToDictionary(x => x.FieldName, StringComparer.Ordinal);

        foreach (var fieldName in previousFields.Keys.Union(currentFields.Keys).OrderBy(x => x, StringComparer.Ordinal))
        {
            previousFields.TryGetValue(fieldName, out var oldField);
            currentFields.TryGetValue(fieldName, out var newField);
            if (oldField is null || newField is null)
            {
                fields.Add(new QualityFieldRegression { FieldName = fieldName, Status = newField is null ? QualityRegressionStatus.Removed : QualityRegressionStatus.Added, Reasons = new[] { newField is null ? "字段在新结果中消失" : "新增字段" } });
                continue;
            }

            var reasons = new List<string>();
            var coverageDelta = newField.Coverage - oldField.Coverage;
            var oldNullRate = oldField.Count == 0 ? 0 : (double)oldField.NullCount / oldField.Count;
            var newNullRate = newField.Count == 0 ? 0 : (double)newField.NullCount / newField.Count;
            var nullRateDelta = newNullRate - oldNullRate;
            double? uniquenessDelta = null;
            if (oldField.DistinctCount is not null && newField.DistinctCount is not null && oldField.Count > 0 && newField.Count > 0)
                uniquenessDelta = (double)newField.DistinctCount.Value / newField.Count - (double)oldField.DistinctCount.Value / oldField.Count;
            if (coverageDelta <= -0.05) reasons.Add($"覆盖率下降 {coverageDelta:P1}");
            if (nullRateDelta >= 0.05) reasons.Add($"空值率上升 {nullRateDelta:P1}");
            if (uniquenessDelta is <= -0.05) reasons.Add($"唯一率下降 {uniquenessDelta.Value:P1}");
            if (newField.RangeViolationCount > oldField.RangeViolationCount) reasons.Add($"范围违规增加 {newField.RangeViolationCount - oldField.RangeViolationCount}");
            if (newField.OutlierRatio is not null && oldField.OutlierRatio is not null && newField.OutlierRatio - oldField.OutlierRatio >= 0.05) reasons.Add($"异常值率上升 {newField.OutlierRatio - oldField.OutlierRatio:P1}");
            fields.Add(new QualityFieldRegression { FieldName = fieldName, Status = reasons.Count == 0 ? QualityRegressionStatus.Stable : QualityRegressionStatus.Regressed, CoverageDelta = coverageDelta, NullRateDelta = nullRateDelta, UniquenessDelta = uniquenessDelta, Reasons = reasons });
        }

        var oldViolations = previous.QualityViolations.GroupBy(x => new ViolationKey(x.Field, x.Check)).ToDictionary(x => x.Key, x => x.Sum(v => v.Count));
        var newViolations = current.QualityViolations.GroupBy(x => new ViolationKey(x.Field, x.Check)).ToDictionary(x => x.Key, x => x.Sum(v => v.Count));
        var violationChanges = new List<QualityViolationRegression>();
        foreach (var key in oldViolations.Keys.Union(newViolations.Keys))
        {
            oldViolations.TryGetValue(key, out var oldCount);
            newViolations.TryGetValue(key, out var newCount);
            if (newCount <= oldCount) continue;
            violationChanges.Add(new QualityViolationRegression { FieldName = key.Field, Check = key.Check, PreviousCount = oldCount, CurrentCount = newCount, Delta = newCount - oldCount, IsNew = oldCount == 0 });
        }

        var scoreDelta = previousScore.HasValue && currentScore.HasValue ? Math.Round(currentScore.Value - previousScore.Value, 2) : 0;
        var issueDelta = current.QualityViolations.Count - previous.QualityViolations.Count;
        var status = scoreDelta <= -10 || violationChanges.Count >= 5 ? QualityRegressionStatus.Severe
            : scoreDelta <= -3 || fields.Any(x => x.Status == QualityRegressionStatus.Regressed) ? QualityRegressionStatus.Regressed
            : scoreDelta >= 3 ? QualityRegressionStatus.Improved : QualityRegressionStatus.Stable;
        return new QualityRegressionReport { Status = status, ScoreDelta = scoreDelta, IssueDelta = issueDelta, PreviousObjectCount = previous.TotalObjects, CurrentObjectCount = current.TotalObjects, FieldRegressions = fields.Where(x => x.Status != QualityRegressionStatus.Stable).ToList(), ViolationRegressions = violationChanges.OrderByDescending(x => x.Delta).ToList() };
    }
    private readonly record struct ViolationKey(string Field, string Check);
}

public enum QualityRegressionStatus { Stable, Improved, Regressed, Severe, Added, Removed }
public sealed class QualityRegressionReport
{
    public QualityRegressionStatus Status { get; init; }
    public double ScoreDelta { get; init; }
    public int IssueDelta { get; init; }
    public long PreviousObjectCount { get; init; }
    public long CurrentObjectCount { get; init; }
    public IReadOnlyList<QualityFieldRegression> FieldRegressions { get; init; } = Array.Empty<QualityFieldRegression>();
    public IReadOnlyList<QualityViolationRegression> ViolationRegressions { get; init; } = Array.Empty<QualityViolationRegression>();
}
public sealed class QualityFieldRegression
{
    public string FieldName { get; init; } = string.Empty;
    public QualityRegressionStatus Status { get; init; }
    public double CoverageDelta { get; init; }
    public double NullRateDelta { get; init; }
    public double? UniquenessDelta { get; init; }
    public IReadOnlyList<string> Reasons { get; init; } = Array.Empty<string>();
}
public sealed class QualityViolationRegression
{
    public string FieldName { get; init; } = string.Empty;
    public string Check { get; init; } = string.Empty;
    public long PreviousCount { get; init; }
    public long CurrentCount { get; init; }
    public long Delta { get; init; }
    public bool IsNew { get; init; }
}
