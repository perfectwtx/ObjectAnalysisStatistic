using System.Text.Json.Serialization;

namespace ObjectAnalyzer.Analysis;

/// <summary>分析完成时的关键统计指标（不含值）。</summary>
public sealed class AuditResultSummary
{
    [JsonPropertyName("totalObjects")] public long TotalObjects { get; set; }
    [JsonPropertyName("totalUniqueObjects")] public long TotalUniqueObjects { get; set; }
    [JsonPropertyName("totalFields")] public int TotalFields { get; set; }
    [JsonPropertyName("qualityViolationCount")] public int QualityViolationCount { get; set; }
    [JsonPropertyName("piiFieldCount")] public int PiiFieldCount { get; set; }
    [JsonPropertyName("piiComplianceLevel")] public string? PiiComplianceLevel { get; set; }
    [JsonPropertyName("schemaBreakingChangeCount")] public int SchemaBreakingChangeCount { get; set; }
    [JsonPropertyName("outlierOffenderCount")] public int OutlierOffenderCount { get; set; }
    [JsonPropertyName("transformFailureCount")] public long TransformFailureCount { get; set; }
    [JsonPropertyName("elapsedMs")] public long ElapsedMs { get; set; }
}
