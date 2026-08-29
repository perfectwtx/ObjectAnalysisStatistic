using System.Text.Json.Serialization;

namespace ObjectAnalyzer.Analysis;

/// <summary>单个属性的 before/after 描述。</summary>
public sealed class SchemaFieldChangeItem
{
    /// <summary>属性名（如 "primaryType" / "coverage" / "minNumber" / "maxNumber" / "nullable" / "semanticType"）。</summary>
    [JsonPropertyName("property")]
    public string Property { get; set; } = "";

    /// <summary>是否破坏性变化（类型/可空性变化，coverage 大幅下降等）。</summary>
    [JsonPropertyName("breaking")]
    public bool Breaking { get; set; }

    /// <summary>人类可读的简短描述（包含 before/after 值与判定方向）。</summary>
    [JsonPropertyName("description")]
    public string Description { get; set; } = "";
}
