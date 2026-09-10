using ObjectAnalyzer.Analysis;

namespace ObjectAnalyzer.Platform;

/// <summary>统一执行质量门禁，供 CLI、API、Web 等入口复用。</summary>
public static class QualityGateEvaluator
{
    public static QualityGateResult Evaluate(AnalysisResult result, QualityGate gate)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(gate);

        var metrics = QualityMetricsCalculator.Calculate(result);
        var failures = new List<QualityGateFailure>();

        CheckMinimum(failures, "OverallScore", metrics.OverallScore, gate.OverallScoreAtLeast, "综合质量分低于门禁阈值。" );
        CheckMinimum(failures, "Completeness", metrics.Completeness, gate.CompletenessAtLeast, "完整性低于门禁阈值。" );
        CheckMinimum(failures, "Validity", metrics.Validity, gate.ValidityAtLeast, "有效性低于门禁阈值。" );
        CheckMinimum(failures, "Uniqueness", metrics.Uniqueness, gate.UniquenessAtLeast, "唯一性低于门禁阈值。" );
        CheckMinimum(failures, "Consistency", metrics.Consistency, gate.ConsistencyAtLeast, "一致性低于门禁阈值。" );
        CheckMinimum(failures, "AnomalyControl", metrics.AnomalyControl, gate.AnomalyControlAtLeast, "异常控制低于门禁阈值。" );

        if (gate.IssueCountAtMost is { } maxIssues && result.QualityViolations.Count > maxIssues)
        {
            failures.Add(new QualityGateFailure(
                "IssueCount",
                $"质量问题数量超过门禁上限，当前 {result.QualityViolations.Count}，允许 {maxIssues}。",
                result.QualityViolations.Count,
                maxIssues));
        }

        if (gate.BreakingSchemaChangeAllowed == false && result.SchemaDiff?.HasBreakingChanges == true)
        {
            failures.Add(new QualityGateFailure(
                "BreakingSchemaChange",
                "检测到破坏性 Schema 变更，而当前门禁禁止该变更。"));
        }

        return failures.Count == 0
            ? QualityGateResult.Success
            : new QualityGateResult(false, failures);
    }

    private static void CheckMinimum(
        ICollection<QualityGateFailure> failures,
        string rule,
        double actual,
        double? expected,
        string message)
    {
        if (expected is { } threshold && actual < threshold)
        {
            failures.Add(new QualityGateFailure(rule, message, actual, threshold));
        }
    }
}
