using ObjectAnalyzer.Analysis;
using ObjectAnalyzer.Platform;

namespace ObjectAnalyzer.Tests.Platform;

public sealed class QualityMetricsCalculatorTests
{
    [Fact]
    public void Calculate_WhenNoFields_ReturnsPerfectMetrics()
    {
        var result = new AnalysisResult();

        var metrics = QualityMetricsCalculator.Calculate(result);

        Assert.Equal(100, metrics.Completeness);
        Assert.Equal(100, metrics.Validity);
        Assert.Equal(100, metrics.Uniqueness);
        Assert.Equal(100, metrics.Consistency);
        Assert.Equal(100, metrics.AnomalyControl);
        Assert.Equal(100, metrics.OverallScore);
        Assert.Equal(metrics.OverallScore, QualityScoreCalculator.Calculate(result));
    }
}
