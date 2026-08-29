using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ObjectAnalyzer.Analysis;

/// <summary>
/// 值解析规则：针对字段/列内容按 parseType 决定是否和如何解析 JSON，适用于 Excel / CSV / 其他来源的值级解析。
/// </summary>
public class ValueParserConfig
{
    /// <summary>来源字段/列名（如 "A"、"payload"）。</summary>
    [JsonPropertyName("source")]
    public string Source { get; set; } = "";

    /// <summary>解析类型：none / auto / json / json-string。</summary>
    [JsonPropertyName("parseType")]
    public string ParseType { get; set; } = "auto";

    /// <summary>是否对该来源解析出的对象/数组做扁平化。null 表示沿用全局 flatten 配置。</summary>
    [JsonPropertyName("flatten")]
    public bool? Flatten { get; set; }

    /// <summary>表头模式：1（默认）首行值为列名，source/字段前缀用表头文字；0 无表头，列以字母 A/B/C 命名，source/字段前缀用列字母。</summary>
    [JsonPropertyName("header")]
    public int? Header { get; set; }
}
