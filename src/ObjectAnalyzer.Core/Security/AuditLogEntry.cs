using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ObjectAnalyzer.Analysis;

/// <summary>
/// 单条审计日志记录（NDJSON 格式，每行一条 JSON 对象）。
/// 字段设计目标：
///  - 自描述：包含 schema/version，便于后续解析与归档；
///  - 不含 PII：仅记录输入文件的 SHA-256、记录数与字段名，绝不记录字段值；
///  - 可关联：含 runId 串联同一次分析的多条事件（start/finish/error）。
/// </summary>
public sealed class AuditLogEntry
{
    /// <summary>日志 schema 版本，便于后续解析兼容性管理。</summary>
    [JsonPropertyName("schema")]
    public string Schema { get; set; } = "object-analyzer.audit/v1";

    /// <summary>UTC 时间戳（ISO 8601，毫秒精度）。</summary>
    [JsonPropertyName("timestamp")]
    public string Timestamp { get; set; } = "";

    /// <summary>事件类型：analysis_start / analysis_finish / analysis_error / analysis_cancel。</summary>
    [JsonPropertyName("event")]
    public string Event { get; set; } = "";

    /// <summary>本次分析运行唯一 ID（用于串联多条事件）。</summary>
    [JsonPropertyName("runId")]
    public string RunId { get; set; } = "";

    /// <summary>操作者标识（--operator；缺省为 "anonymous"，允许运维侧回填）。</summary>
    [JsonPropertyName("operator")]
    public string Operator { get; set; } = "anonymous";

    /// <summary>工具版本（ObjectAnalyzer 版本）。</summary>
    [JsonPropertyName("toolVersion")]
    public string ToolVersion { get; set; } = "";

    /// <summary>输入来源描述：文件路径 / db://provider?query / 派生表达式后缀（不含敏感值）。</summary>
    [JsonPropertyName("inputSource")]
    public string InputSource { get; set; } = "";

    /// <summary>输入内容的 SHA-256 哈希（hex），用于溯源；不记录原文。空时表示数据库直连等无法哈希的来源。</summary>
    [JsonPropertyName("inputHash")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InputHash { get; set; }

    /// <summary>输入字节数（仅当 inputHash 非空时存在）。</summary>
    [JsonPropertyName("inputBytes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? InputBytes { get; set; }

    /// <summary>本次分析使用的关键选项快照（不含敏感值；密钥/PEM 等不写入）。</summary>
    [JsonPropertyName("options")]
    public AuditOptionsSnapshot Options { get; set; } = new();

    /// <summary>分析完成时的结果摘要（仅 start 不含）。</summary>
    [JsonPropertyName("result")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AuditResultSummary? Result { get; set; }

    /// <summary>错误或取消的描述（仅 error/cancel 事件存在；不包含 PII）。</summary>
    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; set; }

    /// <summary>退出码（仅 finish/error 事件存在）。</summary>
    [JsonPropertyName("exitCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ExitCode { get; set; }
}
