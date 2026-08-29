using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace ObjectAnalyzer.Analysis;

/// <summary>一对强相关字段。</summary>
public sealed class StrongCorrelationPair
{
    [JsonPropertyName("fieldA")] public string FieldA { get; set; } = "";
    [JsonPropertyName("fieldB")] public string FieldB { get; set; } = "";
    [JsonPropertyName("pearson")] public double? Pearson { get; set; }
    [JsonPropertyName("spearman")] public double? Spearman { get; set; }
    [JsonPropertyName("samples")] public long Samples { get; set; }
}
