using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ObjectAnalyzer.Analysis;

/// <summary>
/// 字段规则定义，用于指定字段的默认值、主导类型和枚举约束。
/// </summary>
public class FieldRule
{
    /// <summary>指定字段默认值 (覆盖自动推断)。</summary>
    [JsonPropertyName("defaultValue")]
    public object? DefaultValue { get; set; }

    /// <summary>指定主导类型 (覆盖自动检测)。</summary>
    [JsonPropertyName("primaryType")]
    public string? PrimaryType { get; set; }

    /// <summary>枚举约束，支持混合类型 (数值 + 字符串)。</summary>
    [JsonPropertyName("enumValues")]
    public List<JsonElement>? EnumValues { get; set; }
}

/// <summary>
/// 分析规则配置，包含所有字段规则的集合。
/// </summary>
public class AnalysisRules
{
    [JsonPropertyName("fields")]
    public Dictionary<string, FieldRule> Fields { get; set; } = new();
}
