using System.Text.Json.Serialization;

namespace ObjectAnalyzer.Analysis;

/// <summary>单个字段的差异描述（baseline vs current，列出发生变化的属性子项）。</summary>
public sealed class SchemaFieldChange
{
    /// <summary>字段路径。</summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    /// <summary>是否包含破坏性变更（类型变化 / nullable 收紧 / coverage 大幅下降）。</summary>
    [JsonPropertyName("breaking")]
    public bool Breaking { get; set; }

    /// <summary>变化的属性列表（"primaryType" / "semanticType" / "nullable" / "coverage" / "minNumber" / "maxNumber" / ...）。</summary>
    [JsonPropertyName("changes")]
    public List<SchemaFieldChangeItem> Changes { get; set; } = new();
}
