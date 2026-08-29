using System.Text.Json.Serialization;

namespace ObjectAnalyzer.Analysis;

/// <summary>
/// 模糊去重结果：基于"规范化键 + 词袋 + 公共前缀"的近邻聚类发现的相似重复组。
/// 与精确去重（<see cref="DuplicateObjectStatistic"/>）互补：后者检测字节级相同的对象；
/// 本类检测"形态相似但书写差异"的对象（大小写/空格/标点/轻微错别字）。
/// </summary>
public sealed class FuzzyDuplicateGroup
{
    /// <summary>组代表的对象 JSON（首条样本）。</summary>
    [JsonPropertyName("representativeJson")]
    public string RepresentativeJson { get; set; } = "";

    /// <summary>规范化键（NFKC + 折叠 + trim + 大小写归一）。</summary>
    [JsonPropertyName("canonicalKey")]
    public string CanonicalKey { get; set; } = "";

    /// <summary>组内样本数（包含代表样本）。</summary>
    [JsonPropertyName("count")]
    public long Count { get; set; }

    /// <summary>组内样本的相似度（0~1，1=完全相等）。</summary>
    [JsonPropertyName("averageSimilarity")]
    public double AverageSimilarity { get; set; }

    /// <summary>组内最多 5 个样例值（截断自原始样本）。</summary>
    [JsonPropertyName("examples")]
    public List<string> Examples { get; set; } = new();
}
