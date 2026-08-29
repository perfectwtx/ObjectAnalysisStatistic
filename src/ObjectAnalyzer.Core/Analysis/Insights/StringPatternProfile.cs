using System.Text.Json.Serialization;

namespace ObjectAnalyzer.Analysis;

/// <summary>
/// 字符串模式自动归纳结果：对一组字符串归纳出形态模板（如 "###-####" / "1[3-9]########"），
/// 并报告各模板的覆盖率。典型应用：手机号、邮编、订单号、身份证等。
/// </summary>
public sealed class StringPatternProfile
{
    /// <summary>被分析样本数（流式收集，无重复去重）。</summary>
    [JsonPropertyName("totalSamples")]
    public long TotalSamples { get; set; }

    /// <summary>主导模板（覆盖样本数最多者）。无样本时为 null。</summary>
    [JsonPropertyName("dominantPattern")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StringPatternTemplate? DominantPattern { get; set; }

    /// <summary>模板列表（按样本数降序；超过 TopN 上限仅保留前 N 个，其余合并到 "其他"）。</summary>
    [JsonPropertyName("patterns")]
    public List<StringPatternTemplate> Patterns { get; set; } = new();

    /// <summary>未被归纳到任何 Top 模板的样本数。TotalSamples − sum(Patterns.Count)。</summary>
    [JsonPropertyName("otherCount")]
    public long OtherCount { get; set; }
}
