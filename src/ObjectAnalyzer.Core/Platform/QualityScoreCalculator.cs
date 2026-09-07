using ObjectAnalyzer.Analysis;

namespace ObjectAnalyzer.Platform;

/// <summary>根据现有分析结果计算轻量级数据质量总分。</summary>
public static class QualityScoreCalculator
{
    public static double Calculate(AnalysisResult result)
    {
        if (result == null) return 0;

        var fields = result.FieldStatistics;
        if (fields.Count == 0 || result.TotalObjects <= 0) return 100;

        double completeness = fields.Average(f => Math.Clamp(f.Coverage * 100d, 0d, 100d));
        double validity = result.QualityViolations.Count == 0
            ? 100
            : Math.Clamp(100d - result.QualityViolations.Count * 100d / Math.Max(1, result.TotalObjects), 0d, 100d);
        double uniqueness = result.TotalObjects == 0
            ? 100
            : Math.Clamp(result.TotalUniqueObjects * 100d / result.TotalObjects, 0d, 100d);
        double consistency = result.SchemaDiff?.HasBreakingChanges == true ? 60 : 100;
        double anomaly = fields.Count == 0
            ? 100
            : Math.Clamp(100d - fields.Sum(f => f.OutlierRatio ?? 0d) * 100d / fields.Count, 0d, 100d);

        return Math.Round(
            completeness * 0.30 + validity * 0.30 + uniqueness * 0.15 + consistency * 0.15 + anomaly * 0.10,
            2);
    }
}
