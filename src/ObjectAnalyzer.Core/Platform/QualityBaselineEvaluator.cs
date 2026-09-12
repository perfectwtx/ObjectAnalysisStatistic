namespace ObjectAnalyzer.Platform;

/// <summary>负责将一次分析的质量指标与数据源基线进行比较。</summary>
public static class QualityBaselineEvaluator
{
    public static QualityBaselineEvaluation Evaluate(QualityBaseline baseline, QualityMetrics actual)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(actual);
        baseline.Validate();

        var failures = new List<QualityBaselineFailure>();
        AddFailure(failures, "OverallScore", actual.OverallScore, baseline.MinimumOverallScore);
        AddFailure(failures, "Completeness", actual.Completeness, baseline.MinimumCompleteness);
        AddFailure(failures, "Validity", actual.Validity, baseline.MinimumValidity);
        AddFailure(failures, "Uniqueness", actual.Uniqueness, baseline.MinimumUniqueness);
        AddFailure(failures, "Consistency", actual.Consistency, baseline.MinimumConsistency);
        AddFailure(failures, "AnomalyControl", actual.AnomalyControl, baseline.MinimumAnomalyControl);

        return new QualityBaselineEvaluation(
            baseline.SourceName,
            failures.Count == 0,
            actual,
            failures);
    }

    private static void AddFailure(
        ICollection<QualityBaselineFailure> failures,
        string metric,
        double actual,
        double? minimum)
    {
        if (minimum.HasValue && actual < minimum.Value)
            failures.Add(new QualityBaselineFailure(metric, actual, minimum.Value));
    }
}
