using System.Collections.Concurrent;

namespace ObjectAnalyzer.Analysis;

/// <summary>
/// 分析作业注册中心：把 jobId ↔ CancellationTokenSource / 结果 关联起来，
/// 供 CLI / API / Web 三端共享"取消正在进行的分析"能力。
///
/// 设计约束：
/// - 进程内单例（用静态实例，Core 不引入 DI 容器，保持纯逻辑层不依赖框架）；
/// - 线程安全：所有访问通过 ConcurrentDictionary；
/// - 自动清理：作业进入终态（Cancelled / Faulted / Completed）后保留 5 分钟（可配置）
///   以便客户端轮询 / 拉取结果；超时后由下一次 Register 调用清扫。
/// - 不持久化：进程崩溃即丢；服务重启后所有 job 视为已取消，客户端需重新发起。
/// </summary>
public sealed class AnalysisJobRegistry
{
    private static readonly AnalysisJobRegistry _instance = new();
    public static AnalysisJobRegistry Instance => _instance;

    /// <summary>默认清理窗口：5 分钟。</summary>
    public static readonly TimeSpan DefaultRetention = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, AnalysisJob> _jobs = new(StringComparer.Ordinal);

    /// <summary>注册一个分析作业并返回 jobId。</summary>
    /// <param name="cts">与该作业绑定的取消源；AnalysisCore 内部将其 token 传入分析循环。</param>
    /// <param name="meta">调用方写入的元数据（数据文件名 / 格式 / 操作者等），用于 cancel/status 端点回显。</param>
    public string Register(CancellationTokenSource cts, AnalysisJobMeta meta)
    {
        SweepExpired();
        string id = Guid.NewGuid().ToString("N");
        _jobs[id] = new AnalysisJob(id, cts, meta, DateTimeOffset.UtcNow);
        return id;
    }

    /// <summary>查询作业状态；找不到（已清理 / 不存在）返回 null。</summary>
    public AnalysisJobSnapshot? Get(string jobId)
    {
        if (string.IsNullOrEmpty(jobId)) return null;
        if (!_jobs.TryGetValue(jobId, out var job)) return null;
        return job.ToSnapshot();
    }

    /// <summary>
    /// 取消指定作业：触发其 CancellationTokenSource，返回是否成功。
    /// 作业不存在或已进入终态返回 false。
    /// </summary>
    public bool Cancel(string jobId)
    {
        if (string.IsNullOrEmpty(jobId)) return false;
        if (!_jobs.TryGetValue(jobId, out var job)) return false;
        if (job.IsTerminal) return false;
        try
        {
            job.Cts.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>
    /// 上报作业进度（已处理记录数）。供宿主把管线进度桥接到注册中心，
    /// 状态查询与 SSE 事件流据此回显。作业不存在时静默忽略。
    /// </summary>
    public void ReportProgress(string jobId, long processedObjects)
    {
        if (string.IsNullOrEmpty(jobId)) return;
        if (!_jobs.TryGetValue(jobId, out var job)) return;
        job.ProcessedObjects = processedObjects;
    }

    /// <summary>把分析结果写回作业快照，供异步端点的 GET 拉取。</summary>
    public void Complete(string jobId, AnalysisResult result)
        => Complete(jobId, result, preview: null);

    /// <summary>把分析结果与数据预览样本一并写回作业快照（W8：数据预览页）。</summary>
    /// <param name="piiRedacted">出站打码是否生效（结果离开服务前已按策略打码）。
    /// 调用方在出站边界关闭打码（piiOff）时必须传 false，供状态查询端点回显留痕。</param>
    /// <param name="pii">出站打码策略（可选）：携带解析后的敏感字段元数据，供状态查询端点回显。</param>
    public void Complete(string jobId, AnalysisResult result, IReadOnlyList<Dictionary<string, object?>>? preview,
        bool piiRedacted = true, PiiPolicy? pii = null)
    {
        if (string.IsNullOrEmpty(jobId)) return;
        if (!_jobs.TryGetValue(jobId, out var job)) return;
        job.MarkCompleted(result, preview, piiRedacted, pii);
    }

    /// <summary>把异常写回作业快照（供异步端点返回 500）。</summary>
    public void Fail(string jobId, Exception error)
    {
        if (string.IsNullOrEmpty(jobId)) return;
        if (!_jobs.TryGetValue(jobId, out var job)) return;
        job.MarkFailed(error);
    }

    /// <summary>遍历并清掉已超期（> retention 且进入终态）的作业。</summary>
    public int SweepExpired(TimeSpan? retention = null)
    {
        var window = retention ?? DefaultRetention;
        var now = DateTimeOffset.UtcNow;
        int removed = 0;
        foreach (var kvp in _jobs)
        {
            var job = kvp.Value;
            if (!job.IsTerminal) continue;
            if (now - job.TerminalAt < window) continue;
            if (_jobs.TryRemove(kvp.Key, out _))
            {
                job.DisposeCts();
                removed++;
            }
        }
        return removed;
    }

    // ──────────────────────────────────────────────
    // 内部数据结构
    // ──────────────────────────────────────────────

    private sealed class AnalysisJob
    {
        public string Id { get; }
        public CancellationTokenSource Cts { get; }
        public AnalysisJobMeta Meta { get; }
        public DateTimeOffset CreatedAt { get; }
        public DateTimeOffset? TerminalAt { get; private set; }
        public AnalysisStatus Status { get; private set; } = AnalysisStatus.Running;
        public AnalysisResult? Result { get; private set; }
        public IReadOnlyList<Dictionary<string, object?>>? Preview { get; private set; }
        public bool PiiRedacted { get; private set; } = true;
        public PiiPolicy? Pii { get; private set; }
        public Exception? Error { get; private set; }
        public string? ProgressText { get; set; }

        /// <summary>已处理记录数（管线进度桥接写入；volatile 仅供 SSE/轮询读取）。</summary>
        public long ProcessedObjects;

        public AnalysisJob(string id, CancellationTokenSource cts, AnalysisJobMeta meta, DateTimeOffset createdAt)
        {
            Id = id;
            Cts = cts;
            Meta = meta;
            CreatedAt = createdAt;
            // 监听取消请求：作业被 Cancel 时记录状态，便于状态查询回显
            cts.Token.Register(() =>
            {
                if (Status == AnalysisStatus.Running)
                {
                    Status = AnalysisStatus.Cancelled;
                    TerminalAt = DateTimeOffset.UtcNow;
                }
            });
        }

        public bool IsTerminal =>
            Status is AnalysisStatus.Completed or AnalysisStatus.Failed or AnalysisStatus.Cancelled;

        public void MarkCompleted(AnalysisResult result, IReadOnlyList<Dictionary<string, object?>>? preview,
            bool piiRedacted = true, PiiPolicy? pii = null)
        {
            if (IsTerminal) return;
            Result = result;
            Preview = preview;
            PiiRedacted = piiRedacted;
            Pii = pii;
            Status = AnalysisStatus.Completed;
            TerminalAt = DateTimeOffset.UtcNow;
        }

        public void MarkFailed(Exception error)
        {
            if (IsTerminal) return;
            Error = error;
            Status = AnalysisStatus.Failed;
            TerminalAt = DateTimeOffset.UtcNow;
        }

        public void DisposeCts()
        {
            try { Cts.Dispose(); } catch { /* CTS 已释放 */ }
        }

        public AnalysisJobSnapshot ToSnapshot() => new(
            Id: Id,
            Status: Status,
            Meta: Meta,
            CreatedAt: CreatedAt,
            TerminalAt: TerminalAt,
            ProgressText: ProgressText,
            ProcessedObjects: Interlocked.Read(ref ProcessedObjects),
            Result: Result,
            Preview: Preview,
            Error: Error,
            PiiRedacted: PiiRedacted,
            Pii: Pii);
    }
}

/// <summary>作业状态机。</summary>
public enum AnalysisStatus
{
    Running = 0,
    Completed = 1,
    Failed = 2,
    Cancelled = 3
}

/// <summary>注册作业时附带的元数据，cancel/status 端点会回显给客户端。</summary>
public sealed record AnalysisJobMeta(
    string DataFileName,
    string Format,
    string? OperatorId,
    DateTimeOffset SubmittedAt);

/// <summary>作业快照：状态查询 / 拉取结果时使用，Result / Preview 终态前为 null。</summary>
/// <param name="PiiRedacted">出站打码是否生效；false 表示作业提交时显式关闭了打码（piiOff），响应含原始值。</param>
/// <param name="Pii">出站打码策略（未提供时为 null）：携带解析后的敏感字段元数据供端点回显。</param>
public sealed record AnalysisJobSnapshot(
    string Id,
    AnalysisStatus Status,
    AnalysisJobMeta Meta,
    DateTimeOffset CreatedAt,
    DateTimeOffset? TerminalAt,
    string? ProgressText,
    long ProcessedObjects,
    AnalysisResult? Result,
    IReadOnlyList<Dictionary<string, object?>>? Preview,
    Exception? Error,
    bool PiiRedacted = true,
    PiiPolicy? Pii = null);
