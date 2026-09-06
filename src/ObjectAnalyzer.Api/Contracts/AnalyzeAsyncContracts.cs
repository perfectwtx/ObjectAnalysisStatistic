using ObjectAnalyzer.Analysis;

namespace ObjectAnalyzer.Api;

/// <summary>POST /api/analyze/async 成功响应（异步分析作业的 jobId）。</summary>
public sealed record AnalyzeAsyncResponse(
    string JobId,
    AnalysisStatus Status,
    string FileName,
    string Format,
    string? RulesSource,
    DateTimeOffset SubmittedAt)
{
    /// <summary>作业 ID（GUID 无连字符的 32 字符串）。</summary>
    public string JobId { get; init; } = JobId;

    /// <summary>作业状态（提交时一律为 Running）。</summary>
    public AnalysisStatus Status { get; init; } = Status;

    /// <summary>数据文件名（multipart 上传）或占位文本（config / raw）。</summary>
    public string FileName { get; init; } = FileName;

    /// <summary>识别到的数据格式。</summary>
    public string Format { get; init; } = Format;

    /// <summary>规则来源：inline / file；未提供规则时省略。</summary>
    public string? RulesSource { get; init; } = RulesSource;

    /// <summary>作业注册时间（UTC）。</summary>
    public DateTimeOffset SubmittedAt { get; init; } = SubmittedAt;
}

/// <summary>GET /api/analyze/async/{jobId} 响应：作业快照 + 终态时的完整结果。</summary>
/// <remarks>
/// 错误信息 <see cref="Error"/> 已序列化为字符串（"TypeName: message"），避免直接序列化
/// <see cref="System.Exception"/> 时因 TargetSite (MethodBase) 触发 NotSupportedException。
/// </remarks>
public sealed record AnalyzeJobSnapshotResponse(
    string Id,
    AnalysisStatus Status,
    AnalysisJobMeta Meta,
    DateTimeOffset CreatedAt,
    DateTimeOffset? TerminalAt,
    string? ProgressText,
    long ProcessedObjects,
    AnalysisResult? Result,
    IReadOnlyList<Dictionary<string, object?>>? Preview,
    string? Error,
    bool PiiRedacted = true)
{
    /// <summary>作业 ID。</summary>
    public string Id { get; init; } = Id;

    /// <summary>作业状态。</summary>
    public AnalysisStatus Status { get; init; } = Status;

    /// <summary>作业元数据（数据文件名 / 格式 / 操作者 / 提交时间）。</summary>
    public AnalysisJobMeta Meta { get; init; } = Meta;

    /// <summary>作业注册时间（UTC）。</summary>
    public DateTimeOffset CreatedAt { get; init; } = CreatedAt;

    /// <summary>作业进入终态的时间（UTC）；未终态时为 null。</summary>
    public DateTimeOffset? TerminalAt { get; init; } = TerminalAt;

    /// <summary>进度文本（与 /api/analyze 同步端点的 progress 输出一致）。</summary>
    public string? ProgressText { get; init; } = ProgressText;

    /// <summary>终态时为完整 AnalysisResult；运行中或失败时为 null。</summary>
    public AnalysisResult? Result { get; init; } = Result;

    /// <summary>失败时为 "TypeName: message"；其它状态为 null。</summary>
    public string? Error { get; init; } = Error;

    /// <summary>出站打码状态：true = 敏感值已按策略打码（默认）；false = 作业提交时显式关闭了打码（piiOff），响应含原始值。</summary>
    public bool PiiRedacted { get; init; } = PiiRedacted;
}

/// <summary>POST /api/analyze/async/{jobId}/cancel 响应。</summary>
public sealed record AnalyzeCancelResponse(string JobId, bool Cancelled)
{
    /// <summary>被操作的作业 ID。</summary>
    public string JobId { get; init; } = JobId;

    /// <summary>是否成功触发了取消（false = 作业不存在或已终态）。</summary>
    public bool Cancelled { get; init; } = Cancelled;
}
