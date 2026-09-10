namespace ObjectAnalyzer.Analysis.Options;

/// <summary>
/// CI 质量门禁策略：违规 / 异常值比例 / Schema 破坏性变更 / PII 合规等级 / 质量评分。
/// 所有评分阈值采用 0~100 百分制；null 表示不启用对应评分门禁。
/// </summary>
public sealed class QualityGateOptions
{
    /// <summary>存在任一违规即以退出码 3 结束（--fail-on-violations）。</summary>
    public bool FailOnViolations { get; set; }

    /// <summary>任一数值字段的 IQR 异常值比例超过该阈值（0.0~1.0）即以退出码 5 结束（--fail-on-outlier-ratio）。null = 不门禁。</summary>
    public double? FailOnOutlierRatio { get; set; }

    /// <summary>CI Schema 演进门禁（--allow-breaking-schema-changes）：true（默认）= 破坏性变更不触发非零退出码；false = 含破坏性变更时以退出码 6 结束。</summary>
    public bool AllowBreakingSchemaChanges { get; set; } = true;

    /// <summary>CI 合规门禁等级（--fail-on-pii-level：A/B/C）。A=同 B（数据干净时 A 级不触发）；B=B/C 级失败（发现任何敏感字段即失败）；C=仅 C 级失败。null = 不门禁。</summary>
    public string? FailOnPiiLevel { get; set; }

    /// <summary>质量总分低于阈值时触发退出码 7。范围 0~100。</summary>
    public double? FailOnOverallScoreBelow { get; set; }

    /// <summary>完整性低于阈值时触发退出码 7。范围 0~100。</summary>
    public double? FailOnCompletenessBelow { get; set; }

    /// <summary>有效性低于阈值时触发退出码 7。范围 0~100。</summary>
    public double? FailOnValidityBelow { get; set; }

    /// <summary>唯一性低于阈值时触发退出码 7。范围 0~100。</summary>
    public double? FailOnUniquenessBelow { get; set; }

    /// <summary>一致性低于阈值时触发退出码 7。范围 0~100。</summary>
    public double? FailOnConsistencyBelow { get; set; }

    /// <summary>异常控制低于阈值时触发退出码 7。范围 0~100。</summary>
    public double? FailOnAnomalyControlBelow { get; set; }

    /// <summary>是否配置了至少一个质量评分门禁。</summary>
    public bool HasScoreThresholds =>
        FailOnOverallScoreBelow.HasValue ||
        FailOnCompletenessBelow.HasValue ||
        FailOnValidityBelow.HasValue ||
        FailOnUniquenessBelow.HasValue ||
        FailOnConsistencyBelow.HasValue ||
        FailOnAnomalyControlBelow.HasValue;
}
