namespace ObjectAnalyzer.Analysis.Options;

/// <summary>
/// PII 与合规等级：报告导出 + 全局开关。
/// </summary>
public sealed class PiiOptions
{
    /// <summary>导出数据安全合规等级报告 JSON 路径（--pii-report）。</summary>
    public string? PiiPath { get; set; }

    /// <summary>全局关闭 PII 识别与打码（--pii-off），报告输出原始值。</summary>
    public bool PiiOff { get; set; }
}
