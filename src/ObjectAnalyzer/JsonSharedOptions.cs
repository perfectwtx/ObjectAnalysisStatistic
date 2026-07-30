using System.Text.Json;

namespace ObjectAnalyzer;

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
    /// </summary>
    public static readonly JsonSerializerOptions Write = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
