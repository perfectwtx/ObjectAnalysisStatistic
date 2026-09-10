namespace ObjectAnalyzer.Analysis.Gates;

/// <summary>
/// 单个质量门禁的结构化评估结果。
/// </summary>
public readonly record struct QualityGateResult(
    string Name,
    int ExitCode,
    string FailureMessage,
    bool Triggered);

/// <summary>
/// 质量门禁结构化评估器。
/// 复用 <see cref="QualityGateRegistry"/> 中已经注册的门禁实现，不重复维护门禁规则。
/// </summary>
public static class QualityGateEvaluation
{
    /// <summary>
    /// 评估全部已注册门禁，保留每个门禁的通过/失败状态。
    /// </summary>
    public static IReadOnlyList<QualityGateResult> EvaluateAll(
        AnalysisResult result,
        AnalysisOptions options)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(options);

        return QualityGateRegistry.Gates
            .Select(gate => new QualityGateResult(
                gate.Name,
                gate.ExitCode,
                gate.FailureMessage,
                gate.Check(result, options).Triggered))
            .ToList();
    }

    /// <summary>
    /// 获取首个触发的门禁；全部通过时返回 null。
    /// </summary>
    public static QualityGateResult? GetFirstFailure(
        AnalysisResult result,
        AnalysisOptions options)
    {
        var failure = EvaluateAll(result, options).FirstOrDefault(static x => x.Triggered);
        return failure.Triggered ? failure : null;
    }
}
