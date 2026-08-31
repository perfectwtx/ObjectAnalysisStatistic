using System.Text.Json.Serialization;

namespace ObjectAnalyzer.Analysis;

/// <summary>
/// JSON 输入投影路径。整文件为单对象时用 <see cref="Root"/>；流式记录用 <see cref="Record"/>（按记录平铺顶层字段，更省内存）。
/// </summary>
public class JsonPathConfig
{
    /// <summary>整体 JSON 的根 JSONPath（如 <c>$.data[*]</c>）。与 <c>--root-path</c> 对应。</summary>
    [JsonPropertyName("root")]
    public string? Root { get; set; }

    /// <summary>记录级 JSONPath（如 <c>$.payload</c>）。与 <c>--record-path</c> 对应。</summary>
    [JsonPropertyName("record")]
    public string? Record { get; set; }
}
