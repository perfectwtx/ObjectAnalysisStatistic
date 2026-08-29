using System.Text.Json.Serialization;

namespace ObjectAnalyzer.Analysis;

/// <summary>单个 transform 字段的脱敏描述（仅记录字段名 + 转换类型）。</summary>
public sealed class AuditTransformRef
{
    [JsonPropertyName("field")] public string Field { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
}
