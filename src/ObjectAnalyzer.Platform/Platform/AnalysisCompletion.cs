namespace ObjectAnalyzer.Platform;

/// <summary>All records committed with the successful terminal state of an analysis.</summary>
public sealed record AnalysisCompletion(
    AnalysisJob Job,
    QualitySnapshot Snapshot,
    IReadOnlyList<FieldQualitySnapshot> Fields,
    IReadOnlyList<QualityIssue> Issues,
    IReadOnlyList<WebhookDelivery> Deliveries);
