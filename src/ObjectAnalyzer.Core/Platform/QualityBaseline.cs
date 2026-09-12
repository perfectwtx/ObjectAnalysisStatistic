namespace ObjectAnalyzer.Platform;

/// <summary>数据质量基线。基线按数据源保存，用于判断一次分析是否达到最低质量要求。</summary>
public sealed record QualityBaseline(
    string SourceName,
    double? MinimumOverallScore = 90,
    double? MinimumCompleteness = 90,
    double? MinimumValidity = 90,
    double? MinimumUniqueness = 90,
    double? MinimumConsistency = 90,
    double? MinimumAnomalyControl = 90)
{
    /// <summary>验证基线配置是否合法。</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(SourceName))
            throw new ArgumentException("数据源名称不能为空。", nameof(SourceName));

        ValidateThreshold(MinimumOverallScore, nameof(MinimumOverallScore));
        ValidateThreshold(MinimumCompleteness, nameof(MinimumCompleteness));
        ValidateThreshold(MinimumValidity, nameof(MinimumValidity));
        ValidateThreshold(MinimumUniqueness, nameof(MinimumUniqueness));
        ValidateThreshold(MinimumConsistency, nameof(MinimumConsistency));
        ValidateThreshold(MinimumAnomalyControl, nameof(MinimumAnomalyControl));
    }

    private static void ValidateThreshold(double? value, string name)
    {
        if (value is < 0 or > 100)
            throw new ArgumentOutOfRangeException(name, "质量阈值必须位于 0 到 100 之间。 ");
    }
}

/// <summary>一次质量基线评估的失败项。</summary>
public sealed record QualityBaselineFailure(
    string Metric,
    double Actual,
    double Minimum);

/// <summary>一次质量基线评估结果。</summary>
public sealed record QualityBaselineEvaluation(
    string SourceName,
    bool Passed,
    QualityMetrics Actual,
    IReadOnlyList<QualityBaselineFailure> Failures);
