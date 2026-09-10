namespace ObjectAnalyzer.Analysis.Gates;

/// <summary>
/// 质量评分门禁（退出码 7）：按质量总分或指定质量维度阈值阻断分析。
/// 阈值为 0~100 的百分制；未设置的阈值不参与门禁。
/// </summary>
public sealed class QualityScoreGate : IQualityGate
{
    public string Name => "质量评分";
    public int ExitCode => 7;
    public string FailureMessage => "质量评分门禁未通过";

    public GateVerdict Check(AnalysisResult result, AnalysisOptions options)
    {
        var gate = options.Gates;
        if (!gate.HasScoreThresholds)
            return GateVerdict.Pass;

        var metrics = QualityMetricsCalculator.Calculate(result);
        var failures = new List<string>();

        CheckThreshold(failures, "质量总分", metrics.OverallScore, gate.FailOnOverallScoreBelow);
        CheckThreshold(failures, "完整性", metrics.Completeness, gate.FailOnCompletenessBelow);
        CheckThreshold(failures, "有效性", metrics.Validity, gate.FailOnValidityBelow);
        CheckThreshold(failures, "唯一性", metrics.Uniqueness, gate.FailOnUniquenessBelow);
        CheckThreshold(failures, "一致性", metrics.Consistency, gate.FailOnConsistencyBelow);
        CheckThreshold(failures, "异常控制", metrics.AnomalyControl, gate.FailOnAnomalyControlBelow);

        if (failures.Count == 0)
            return GateVerdict.Pass;

        AnalysisServices.Current.Console.MarkupLine(
            $"[red]质量评分未通过（{string.Join("；", failures)}），退出码 7。[/]");
        return new GateVerdict(true);
    }

    private static void CheckThreshold(List<string> failures, string name, double value, double? threshold)
    {
        if (threshold.HasValue && value < threshold.Value)
            failures.Add($"{name} {value:F2} < {threshold.Value:F2}");
    }
}
