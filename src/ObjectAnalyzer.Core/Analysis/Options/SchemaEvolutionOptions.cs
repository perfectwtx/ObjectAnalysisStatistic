namespace ObjectAnalyzer.Analysis.Options;

/// <summary>
/// Schema 演进：导出 canonical Schema 快照 / 与历史快照做 Diff / 报告输出。
/// </summary>
public sealed class SchemaEvolutionOptions
{
    /// <summary>导出 Schema 快照到指定 JSON 路径（--export-schema-snapshot）。</summary>
    public string? SchemaSnapshotPath { get; set; }

    /// <summary>与历史 Schema 快照做 diff 的基线文件路径（--diff-schema）。</summary>
    public string? DiffSchemaBaseline { get; set; }

    /// <summary>Schema diff 结果导出路径（--schema-diff-report）。</summary>
    public string? SchemaDiffReportPath { get; set; }
}
