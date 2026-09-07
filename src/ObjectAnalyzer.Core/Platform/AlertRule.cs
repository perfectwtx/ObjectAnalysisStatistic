namespace ObjectAnalyzer.Platform;

/// <summary>质量异常告警规则。用于统一描述质量阈值、回归和 Webhook 触发条件。</summary>
public sealed class AlertRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public double? QualityScoreBelow { get; set; }
    public double? CompletenessBelow { get; set; }
    public bool CriticalIssue { get; set; }
    public bool BreakingSchemaChange { get; set; }
    public double? DistributionDriftAbove { get; set; }
    /// <summary>质量分相对上一次成功分析下降达到该百分点数时触发。</summary>
    public double? QualityRegressionBelow { get; set; }
    public string? WebhookUrl { get; set; }
}
