namespace ObjectAnalyzer.Analysis.Options;

/// <summary>
/// 输入源：本地文件路径、数据库直连、JSON 投影。
/// 这些字段只能由调用方（CLI / API / Web）显式传入，规则文件不覆盖。
/// </summary>
public sealed class InputSourceOptions
{
    /// <summary>输入文件路径（--input / -i）。</summary>
    public string InputPath { get; set; } = "";

    /// <summary>数据库直连连接字符串（--connection）。非空时进入数据库直连模式。</summary>
    public string? ConnectionString { get; set; }

    /// <summary>数据库直连时执行的 SQL 查询（--query）。</summary>
    public string? Query { get; set; }

    /// <summary>数据库类型（--provider：postgres / mysql / sqlite / sqlserver），缺省由连接字符串推断。</summary>
    public string? DbProvider { get; set; }

    /// <summary>对整体文件应用 JSONPath 表达式（--root-path，RFC 9535）。仅对 JSON 整体文件生效。</summary>
    public string? RootPath { get; set; }

    /// <summary>对每条记录应用 JSONPath 表达式（--record-path）。流式友好，对所有格式生效。</summary>
    public string? RecordPath { get; set; }
}
