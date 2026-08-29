using System.Text.Json.Serialization;

namespace ObjectAnalyzer.Analysis;

/// <summary>时间序列单个桶（与 FieldStatistic.DateHistogram 的桶对齐）。</summary>
public sealed class TimeSeriesBucket
{
    [JsonPropertyName("from")] public DateTimeOffset From { get; set; }
    [JsonPropertyName("to")] public DateTimeOffset To { get; set; }
    [JsonPropertyName("count")] public long Count { get; set; }
}
