using System.Collections.Concurrent;

namespace ObjectAnalyzer.Analysis;

/// <summary>
/// 分析作业运行时注册中心：把持久化 Job ID 与 CancellationTokenSource / 实时结果关联起来，
/// 供 API 的状态查询、SSE 和取消操作使用。
/// </summary>
public sealed class AnalysisJobRegistry
{
    private static readonly AnalysisJobRegistry _instance = new();
    public static AnalysisJobRegistry Instance => _instance;

    public static readonly TimeSpan DefaultRetention = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, AnalysisJob> _jobs = new(StringComparer.Ordinal);

    /// <summary>注册一个运行时作业并生成 Job ID。</summary>
    public string Register(CancellationTokenSource cts, AnalysisJobMeta meta)
    {
        var id = Guid.NewGuid().ToString("N");
        Register(id, cts, meta);
        return id;
    }

    /// <summary>
    /// 使用已有的持久化 Job ID 注册运行时状态。
    /// 这样 API 的 History、SSE、Cancel 使用同一个作业身份。
    /// </summary>
    public void Register(string jobId, CancellationTokenSource cts, AnalysisJobMeta meta)
    {
        if (string.IsNullOrWhiteSpace(jobId)) throw new ArgumentException("Job ID 不能为空。", nameof(jobId));
        ArgumentNullException.ThrowIfNull(cts);
        ArgumentNullException.ThrowIfNull(meta);

        SweepExpired();
        if (!_jobs.TryAdd(jobId, new AnalysisJob(jobId, cts, meta, DateTimeOffset.UtcNow)))
            throw new InvalidOperationException($"运行时作业 {jobId} 已经注册。 ");
    }

    public AnalysisJobSnapshot? Get(string jobId)
    {
        if (string.IsNullOrEmpty(jobId)) return null;
        if (!_jobs.TryGetValue(jobId, out var job)) return null;
        return job.ToSnapshot();
    }

    public bool Cancel(string jobId)
    {
        if (string.IsNullOrEmpty(jobId)) return false;
        if (!_jobs.TryGetValue(jobId, out var job)) return false;
        try
        {
            return job.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    public void ReportProgress(string jobId, long processedObjects)
    {
        if (string.IsNullOrEmpty(jobId)) return;
        if (!_jobs.TryGetValue(jobId, out var job)) return;
        Interlocked.Exchange(ref job.ProcessedObjects, processedObjects);
    }

    public void Complete(string jobId, AnalysisResult result)
        => Complete(jobId, result, preview: null);

    public void Complete(string jobId, AnalysisResult result, IReadOnlyList<Dictionary<string, object?>>? preview,
        bool piiRedacted = true, PiiPolicy? pii = null)
    {
        if (string.IsNullOrEmpty(jobId)) return;
        if (!_jobs.TryGetValue(jobId, out var job)) return;
        job.MarkCompleted(result, preview, piiRedacted, pii);
    }

    public void Fail(string jobId, Exception error)
    {
        if (string.IsNullOrEmpty(jobId)) return;
        if (!_jobs.TryGetValue(jobId, out var job)) return;
        job.MarkFailed(error);
    }

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

    private sealed class AnalysisJob
    {
        public string Id { get; }
        public CancellationTokenSource Cts { get; }
        public AnalysisJobMeta Meta { get; }
        public DateTimeOffset CreatedAt { get; }
        private readonly object _sync = new();

        public DateTimeOffset? TerminalAt { get; private set; }
        public AnalysisStatus Status { get; private set; } = AnalysisStatus.Running;
        public AnalysisResult? Result { get; private set; }
        public IReadOnlyList<Dictionary<string, object?>>? Preview { get; private set; }
        public bool PiiRedacted { get; private set; } = true;
        public PiiPolicy? Pii { get; private set; }
        public Exception? Error { get; private set; }
        public string? ProgressText { get; set; }
        public long ProcessedObjects;

        public AnalysisJob(string id, CancellationTokenSource cts, AnalysisJobMeta meta, DateTimeOffset createdAt)
        {
            Id = id;
            Cts = cts;
            Meta = meta;
            CreatedAt = createdAt;
            cts.Token.Register(() =>
            {
                lock (_sync)
                {
                    if (Status == AnalysisStatus.Running)
                    {
                        Status = AnalysisStatus.Cancelled;
                        TerminalAt = DateTimeOffset.UtcNow;
                    }
                }
            });
        }

        public bool IsTerminal
        {
            get
            {
                lock (_sync)
                {
                    return Status is AnalysisStatus.Completed or AnalysisStatus.Failed or AnalysisStatus.Cancelled;
                }
            }
        }

        public bool Cancel()
        {
            lock (_sync)
            {
                if (Status != AnalysisStatus.Running)
                    return false;

                try
                {
                    Cts.Cancel();
                    return true;
                }
                catch (ObjectDisposedException)
                {
                    return false;
                }
            }
        }

        public void MarkCompleted(AnalysisResult result, IReadOnlyList<Dictionary<string, object?>>? preview,
            bool piiRedacted = true, PiiPolicy? pii = null)
        {
            lock (_sync)
            {
                if (Status != AnalysisStatus.Running) return;
                Result = result;
            Preview = preview;
            PiiRedacted = piiRedacted;
            Pii = pii;
            Status = AnalysisStatus.Completed;
                TerminalAt = DateTimeOffset.UtcNow;
            }
        }

        public void MarkFailed(Exception error)
        {
            lock (_sync)
            {
                if (Status != AnalysisStatus.Running) return;
                Error = error;
            Status = AnalysisStatus.Failed;
                TerminalAt = DateTimeOffset.UtcNow;
            }
        }

        public void DisposeCts()
        {
            try { Cts.Dispose(); } catch { }
        }

        public AnalysisJobSnapshot ToSnapshot()
        {
            lock (_sync)
            {
                return new(
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
    }
}

public enum AnalysisStatus
{
    Running = 0,
    Completed = 1,
    Failed = 2,
    Cancelled = 3
}

public sealed record AnalysisJobMeta(
    string DataFileName,
    string Format,
    string? OperatorId,
    DateTimeOffset SubmittedAt);

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
    bool PiiRedacted,
    PiiPolicy? Pii);
