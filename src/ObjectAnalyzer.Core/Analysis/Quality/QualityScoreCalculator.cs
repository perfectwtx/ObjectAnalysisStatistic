using ObjectAnalyzer.Analysis;

namespace ObjectAnalyzer.Analysis;

/// <summary>根据现有分析结果计算轻量级数据质量总分。</summary>
public static class QualityScoreCalculator
{
    public static double Calculate(AnalysisResult result)
    {
        if (result is null) return 0;
        return QualityMetricsCalculator.Calculate(result).OverallScore;
    }
}
