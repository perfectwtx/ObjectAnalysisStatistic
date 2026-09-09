using ObjectAnalyzer.Analysis;

namespace ObjectAnalyzer.Platform;

/// <summary>根据分析结果判断是否满足质量告警规则。</summary>
public static class AlertEvaluator
{
    public static bool ShouldNotify(AlertRule rule, AnalysisResult result, double score, double? scoreDelta = null)
    {
        if (!rule.Enabled) return false;

        if (rule.QualityScoreBelow.HasValue && score < rule.QualityScoreBelow.Value)
            return true;

        if (rule.QualityRegressionBelow.HasValue && scoreDelta.HasValue
            && scoreDelta.Value <= -Math.Abs(rule.QualityRegressionBelow.Value))
            return true;

        if (rule.CompletenessBelow.HasValue)
        {
            var completeness = result.FieldStatistics.Count == 0
                ? 100d
                : result.FieldStatistics.Average(f => Math.Clamp(f.Coverage * 100d, 0d, 100d));
            if (completeness < rule.CompletenessBelow.Value)
                return true;
        }

        if (rule.CriticalIssue && result.QualityViolations.Count > 0)
            return true;

        if (rule.BreakingSchemaChange && result.SchemaDiff?.HasBreakingChanges == true)
            return true;

        if (rule.DistributionDriftAbove.HasValue
            && result.DistributionDiff?.SignificantDriftCount >= rule.DistributionDriftAbove.Value)
            return true;

        return false;
    }
}
