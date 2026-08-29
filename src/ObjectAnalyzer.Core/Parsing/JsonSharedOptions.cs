using System.Text.Json;

namespace ObjectAnalyzer.Parsing;

/// <summary>
/// Shared JsonSerializerOptions instances used across the project.
/// </summary>
internal static class JsonSharedOptions
{
    /// <summary>
    /// Options for reading/parsing JSON (lenient, case-insensitive).
    /// </summary>
    public static readonly JsonSerializerOptions Read = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Options for writing JSON output (indented, relaxed escaping).
    /// 用于最终报告与重复对象 JSON 存储。
    /// </summary>
    public static readonly JsonSerializerOptions Write = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// 紧凑序列化选项（不缩进）：用于热路径规范化/哈希与 ValueCounts 值键，
    /// 更快且避免在值键中嵌入换行。
    /// </summary>
    public static readonly JsonSerializerOptions WriteCompact = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
