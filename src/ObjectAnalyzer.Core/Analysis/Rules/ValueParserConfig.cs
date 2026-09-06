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

    /// <summary>
    /// 表头所在行号，决定 source 的含义与数据起始行：
    /// 1（默认）首行是表头，source 用首行标题文本，数据从第 2 行开始；
    /// N（≥2）第 N 行是表头，跳过之前的行，数据从第 N+1 行开始，source 用第 N 行的标题文本；
    /// 0 无表头，首行即数据，source 用列字母（A/B/C）。
    /// excel-json 场景每个 valueParser 独立声明自己的表头行（同一张表的不同列可以有不同的表头行）；
    /// 通用 excel/csv 解析是整表概念，多个 source 声明不一致时取第一个声明的值。
    /// </summary>
    [JsonPropertyName("header")]
    public int? Header { get; set; }
}
