namespace ObjectAnalyzer.Analysis;

/// <summary>数据质量各维度指标。</summary>
public sealed record QualityMetrics(
    double Completeness,
    double Validity,
    double Uniqueness,
    double Consistency,
    double AnomalyControl)
{
    public double OverallScore => Math.Round(
        Completeness * 0.30
        + Validity * 0.30
        + Uniqueness * 0.15
        + Consistency * 0.15
        + AnomalyControl * 0.10,
        2);
}
