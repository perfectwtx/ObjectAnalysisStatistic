using System.Text.Json.Serialization;

namespace ObjectAnalyzer.Analysis;

/// <summary>
/// 多格式报告输出。null = 不输出；非 null = 写到该路径（按扩展名自动选择格式）。
/// </summary>
public class OutputsConfig
{
    /// <summary>主报告输出路径（按扩展名路由 .json / .html / .md / .csv / .xlsx / .pdf）。与 <c>-o</c> 对应。</summary>
    [JsonPropertyName("primary")]
    public string? Primary { get; set; }

    /// <summary>结构化违规清单 JSON 输出路径。与 <c>--violations</c> 对应。</summary>
    [JsonPropertyName("violations")]
    public string? Violations { get; set; }

    /// <summary>数据质量报告 JSON 输出路径。与 <c>--quality</c> 对应。</summary>
    [JsonPropertyName("quality")]
    public string? Quality { get; set; }

    /// <summary>Schema 代码生成（<c>"csharp:out.cs"</c> / <c>"typescript:out.ts"</c> / <c>"json-schema:out.json"</c> / <c>"openapi:out.yaml"</c> / <c>"proto:out.proto"</c> / <c>"sql:out.sql"</c>）。与 <c>-s</c> 对应。</summary>
    [JsonPropertyName("schemaCode")]
    public string? SchemaCode { get; set; }
}
