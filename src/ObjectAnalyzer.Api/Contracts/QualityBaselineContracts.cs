using ObjectAnalyzer.Platform;

namespace ObjectAnalyzer.Api;

/// <summary>质量基线保存请求。兼容前端使用的 minOverallScore / minCompleteness 等字段。</summary>
public sealed record QualityBaselineRequest(
    string? SourceName = null,
    double? MinimumOverallScore = 90,
    double? MinimumCompleteness = 90,
    double? MinimumValidity = 90,
    double? MinimumUniqueness = 90,
    double? MinimumConsistency = 90,
    double? MinimumAnomalyControl = 90,
    string? Name = null,
    double? MinOverallScore = null,
    double? MinCompleteness = null,
    double? MinValidity = null,
    double? MinUniqueness = null,
    double? MinConsistency = null,
    double? MinAnomalyControl = null)
{
    public string EffectiveSourceName => !string.IsNullOrWhiteSpace(SourceName)
        ? SourceName!
        : Name ?? string.Empty;

    public QualityBaseline ToModel(string? sourceName = null)
    {
        var effectiveSourceName = string.IsNullOrWhiteSpace(sourceName) ? EffectiveSourceName : sourceName!;
        return new QualityBaseline(
            effectiveSourceName,
            Normalize(MinOverallScore ?? MinimumOverallScore),
            Normalize(MinCompleteness ?? MinimumCompleteness),
            Normalize(MinValidity ?? MinimumValidity),
            Normalize(MinUniqueness ?? MinimumUniqueness),
            Normalize(MinConsistency ?? MinimumConsistency),
            Normalize(MinAnomalyControl ?? MinimumAnomalyControl));
    }

    private static double? Normalize(double? value)
        => value is >= 0 and <= 1 ? value * 100 : value;
}
