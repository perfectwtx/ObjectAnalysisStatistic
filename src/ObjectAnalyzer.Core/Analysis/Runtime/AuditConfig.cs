using System.Text.Json.Serialization;

namespace ObjectAnalyzer.Analysis;

/// <summary>
/// 审计日志：NDJSON 格式，schema 版本 <c>object-analyzer.audit/v1</c>。
/// <para>绝不在审计日志中记录字段值、SM4 / SM2 / AES 密钥或样本数据。</para>
/// </summary>
public class AuditConfig
{
    /// <summary>审计日志输出路径。与 <c>--audit-log</c> 对应。</summary>
    [JsonPropertyName("log")]
    public string? Log { get; set; }

    /// <summary>操作者标识（CI 用户 / 流水线 ID / 邮箱）。默认 <c>anonymous</c>。与 <c>--operator</c> 对应。</summary>
    [JsonPropertyName("operator")]
    public string? Operator { get; set; }
}
