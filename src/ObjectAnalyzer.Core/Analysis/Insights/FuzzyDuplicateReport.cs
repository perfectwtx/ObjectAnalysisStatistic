using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;

namespace ObjectAnalyzer.Analysis;

/// <summary>
/// 模糊去重结果汇总。
/// </summary>
public sealed class FuzzyDuplicateReport
{
    /// <summary>被检查的样本数（按 --fuzzy-dedup-target 指定的字段汇总）。</summary>
    [JsonPropertyName("totalSamples")]
    public long TotalSamples { get; set; }

    /// <summary>相似重复组数。</summary>
    [JsonPropertyName("groupCount")]
    public int GroupCount { get; set; }

    /// <summary>落在相似重复组中的样本数（模糊 "重复" 样本数）。</summary>
    [JsonPropertyName("similarDuplicates")]
    public long SimilarDuplicates { get; set; }

    /// <summary>相似重复组列表（按组样本数降序）。</summary>
    [JsonPropertyName("groups")]
    public List<FuzzyDuplicateGroup> Groups { get; set; } = new();
}
