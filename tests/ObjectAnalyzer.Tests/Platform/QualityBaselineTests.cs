using ObjectAnalyzer.Platform;

namespace ObjectAnalyzer.Tests.Platform;

public sealed class QualityBaselineTests
{
    [Fact]
    public void Evaluate_WhenAllMetricsMeetBaseline_Passes()
    {
        var baseline = new QualityBaseline(
            "orders",
            MinimumOverallScore: 90,
            MinimumCompleteness: 95,
            MinimumValidity: 90,
            MinimumUniqueness: 90,
            MinimumConsistency: 90,
            MinimumAnomalyControl: 90);

        var actual = new QualityMetrics(96, 92, 91, 95, 94);

        var result = QualityBaselineEvaluator.Evaluate(baseline, actual);

        Assert.True(result.Passed);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public void Evaluate_WhenMetricsFallBelowBaseline_ReturnsEachFailure()
    {
        var baseline = new QualityBaseline("orders", 95, 95, 95, 95, 95, 95);
        var actual = new QualityMetrics(90, 94, 93, 92, 91);

        var result = QualityBaselineEvaluator.Evaluate(baseline, actual);

        Assert.False(result.Passed);
        Assert.Equal(6, result.Failures.Count);
        Assert.Contains(result.Failures, x => x.Metric == "OverallScore");
        Assert.Contains(result.Failures, x => x.Metric == "Completeness");
        Assert.Contains(result.Failures, x => x.Metric == "Validity");
        Assert.Contains(result.Failures, x => x.Metric == "Uniqueness");
        Assert.Contains(result.Failures, x => x.Metric == "Consistency");
        Assert.Contains(result.Failures, x => x.Metric == "AnomalyControl");
    }

    [Fact]
    public void Evaluate_WithNullThreshold_DoesNotFailThatMetric()
    {
        var baseline = new QualityBaseline(
            "orders",
            MinimumOverallScore: null,
            MinimumCompleteness: 95,
            MinimumValidity: null,
            MinimumUniqueness: null,
            MinimumConsistency: null,
            MinimumAnomalyControl: null);
        var actual = new QualityMetrics(96, 10, 10, 10, 10);

        var result = QualityBaselineEvaluator.Evaluate(baseline, actual);

        Assert.True(result.Passed);
    }

    [Fact]
    public void Baseline_InvalidThreshold_Throws()
    {
        var baseline = new QualityBaseline("orders", MinimumOverallScore: 101);

        Assert.Throws<ArgumentOutOfRangeException>(() => baseline.Validate());
    }

    [Fact]
    public void Baseline_EmptySourceName_Throws()
    {
        var baseline = new QualityBaseline(" ");

        Assert.Throws<ArgumentException>(() => baseline.Validate());
    }
}
