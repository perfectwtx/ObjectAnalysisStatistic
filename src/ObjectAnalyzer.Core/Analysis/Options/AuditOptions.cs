namespace ObjectAnalyzer.Analysis.Options;

/// <summary>
/// 审计日志：NDJSON 格式，schema 版本 <c>object-analyzer.audit/v1</c>。
/// 绝不在审计日志中记录字段值、SM4 / SM2 / AES 密钥或样本数据。
/// </summary>
public sealed class AuditOptions
{
    /// <summary>审计日志输出路径（--audit-log）。每行一条 NDJSON 事件。</summary>
    public string? AuditLogPath { get; set; }

    /// <summary>操作者标识（CI 用户 / 流水线 ID / 邮箱，--operator）。默认 <c>anonymous</c>。</summary>
    public string Operator { get; set; } = "anonymous";
}
