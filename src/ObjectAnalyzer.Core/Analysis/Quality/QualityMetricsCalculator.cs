using ObjectAnalyzer.Analysis;

namespace ObjectAnalyzer.Analysis;

/// <summary>从分析结果计算数据质量各维度指标。</summary>
public static class QualityMetricsCalculator
{
    public static QualityMetrics Calculate(AnalysisResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var fields = result.FieldStatistics;
        if (fields.Count == 0 || result.TotalObjects <= 0)
            return new QualityMetrics(100, 100, 100, 100, 100);

        var completeness = fields.Average(f => Math.Clamp(f.Coverage * 100d, 0d, 100d));
        var validity = result.QualityViolations.Count == 0
            ? 100d
            : Math.Clamp(100d - result.QualityViolations.Count * 100d / Math.Max(1, result.TotalObjects), 0d, 100d);
        var uniqueness = Math.Clamp(result.TotalUniqueObjects * 100d / result.TotalObjects, 0d, 100d);
        var consistency = result.SchemaDiff?.HasBreakingChanges == true ? 60d : 100d;
        var anomalyControl = Math.Clamp(100d - fields.Sum(f => f.OutlierRatio ?? 0d) * 100d / fields.Count, 0d, 100d);

        return new QualityMetrics(completeness, validity, uniqueness, consistency, anomalyControl);
    }
}
