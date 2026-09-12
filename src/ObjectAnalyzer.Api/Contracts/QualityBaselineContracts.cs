using ObjectAnalyzer.Platform;

namespace ObjectAnalyzer.Api;

/// <summary>质量基线保存请求。</summary>
public sealed record QualityBaselineRequest(
    string SourceName,
    double? MinimumOverallScore = 90,
    double? MinimumCompleteness = 90,
    double? MinimumValidity = 90,
    double? MinimumUniqueness = 90,
    double? MinimumConsistency = 90,
    double? MinimumAnomalyControl = 90)
{
    public QualityBaseline ToModel() => new(
        SourceName,
        MinimumOverallScore,
        MinimumCompleteness,
        MinimumValidity,
        MinimumUniqueness,
        MinimumConsistency,
        MinimumAnomalyControl);
}
