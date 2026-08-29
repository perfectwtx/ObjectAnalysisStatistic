using System.Text.Json.Serialization;

namespace ObjectAnalyzer.Analysis;

/// <summary>单个字符串模板及其覆盖统计。</summary>
public sealed class StringPatternTemplate
{
    /// <summary>模板字符串：# 表示数字、? 表示字母、* 表示字母数字、其余字面量保留。</summary>
    [JsonPropertyName("template")]
    public string Template { get; set; } = "";

    /// <summary>匹配该模板的样本数。</summary>
    [JsonPropertyName("count")]
    public long Count { get; set; }

    /// <summary>覆盖率（count / total），0.0~1.0。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Ratio { get; set; }

    /// <summary>匹配该模板的样例值（最多 3 个）。</summary>
    [JsonPropertyName("examples")]
    public List<string> Examples { get; set; } = new();

    public override string ToString() => $"{Template} ×{Count}";
}
