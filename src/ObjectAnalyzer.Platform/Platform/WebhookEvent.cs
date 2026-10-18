namespace ObjectAnalyzer.Platform;

/// <summary>质量平台对外通知事件。</summary>
public sealed class WebhookEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string EventType { get; set; } = string.Empty;
    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;
    public Guid JobId { get; set; }
    public string Source { get; set; } = string.Empty;
    public double? PreviousScore { get; set; }
    public double? CurrentScore { get; set; }
    public int IssueCount { get; set; }
}
