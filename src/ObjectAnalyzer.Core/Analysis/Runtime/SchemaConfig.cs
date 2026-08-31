using System.Text.Json.Serialization;

namespace ObjectAnalyzer.Analysis;

/// <summary>
/// Schema 演进：导出 canonical Schema 快照、与历史快照做结构 Diff、是否允许破坏性变更。
/// </summary>
public class SchemaConfig
{
    /// <summary>导出 Schema 快照到该路径。与 <c>--export-schema-snapshot</c> 对应。</summary>
    [JsonPropertyName("snapshot")]
    public string? Snapshot { get; set; }

    /// <summary>作为 Diff 基线的历史快照路径。与 <c>--diff-schema</c> 对应。</summary>
    [JsonPropertyName("diffBaseline")]
    public string? DiffBaseline { get; set; }

    /// <summary>Schema Diff 结果报告输出路径。与 <c>--schema-diff-report</c> 对应。</summary>
    [JsonPropertyName("diffReport")]
    public string? DiffReport { get; set; }

    /// <summary>是否允许破坏性变更（false 时退出码 6）。默认 true。与 <c>--allow-breaking-schema-changes</c> 对应。</summary>
    [JsonPropertyName("allowBreakingChanges")]
    public bool? AllowBreakingChanges { get; set; }
}
