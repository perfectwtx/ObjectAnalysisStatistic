namespace ObjectAnalyzer.Platform;

/// <summary>质量异常告警规则。第一阶段用于统一描述 Webhook 触发条件。</summary>
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
    public string? WebhookUrl { get; set; }
}
