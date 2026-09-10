namespace ObjectAnalyzer.Platform;

/// <summary>质量门禁配置。</summary>
public sealed record QualityGate
{
    /// <summary>最低综合质量分。</summary>
    public double? OverallScoreAtLeast { get; init; }

    /// <summary>最低完整性分。</summary>
    public double? CompletenessAtLeast { get; init; }

    /// <summary>最低有效性分。</summary>
    public double? ValidityAtLeast { get; init; }

    /// <summary>最低唯一性分。</summary>
    public double? UniquenessAtLeast { get; init; }

    /// <summary>最低一致性分。</summary>
    public double? ConsistencyAtLeast { get; init; }

    /// <summary>最低异常控制分。</summary>
    public double? AnomalyControlAtLeast { get; init; }

    /// <summary>允许的最大质量问题数量。</summary>
    public int? IssueCountAtMost { get; init; }

    /// <summary>是否禁止存在破坏性 Schema 变更。</summary>
    public bool? BreakingSchemaChangeAllowed { get; init; }
}

/// <summary>质量门禁失败项。</summary>
public sealed record QualityGateFailure(
    string Rule,
    string Message,
    double? Actual = null,
    double? Expected = null);

/// <summary>质量门禁执行结果。</summary>
public sealed record QualityGateResult(
    bool Passed,
    IReadOnlyList<QualityGateFailure> Failures)
{
    public static QualityGateResult Success { get; } = new(true, Array.Empty<QualityGateFailure>());
}
