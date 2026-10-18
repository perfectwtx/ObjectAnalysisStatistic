using ObjectAnalyzer.Parsing;

namespace ObjectAnalyzer.Analysis.Pipeline;

/// <summary>
/// 统一分析管线的运行期上下文（W1/W3 落点）：随 <see cref="AnalysisPipeline.Execute"/> 传入的
/// per-run 状态——内存预算、续跑起始位置、宿主进度桥接、预览捕获；
/// 管线执行后回填实际格式与已分析记录数（供 CLI 检查点落盘）。
/// </summary>
public sealed class AnalysisRunContext
{
    private int _used;
    private readonly List<Exception> _observerErrors = new();
    public Guid RunId { get; } = Guid.NewGuid();
    public Action<AnalysisEvent>? EventSink { get; init; }
    public IReadOnlyList<Exception> ObserverErrors => _observerErrors.AsReadOnly();
    internal AnalysisStage CurrentStage { get; set; } = AnalysisStage.Preparing;

    internal void Begin()
    {
        if (Interlocked.Exchange(ref _used, 1) != 0)
            throw new InvalidOperationException("A run context can only be executed once.");
    }

    // Notifications are synchronous. Host failures cannot change the computation or its terminal outcome.
    internal void Notify(Action notification)
    {
        try { notification(); }
        catch (Exception exception)
        {
            if (_observerErrors.Count < 32) _observerErrors.Add(exception);
        }
    }

    internal void Publish(AnalysisEvent notification) => Notify(() => EventSink?.Invoke(notification));

    public FieldAnalyzerRegistry? Analyzers { get; init; }

    public AnalysisAccumulator? InitialState { get; init; }

    /// <summary>可选的独占内存监控实例；null 时按执行配置创建本次运行的监控。</summary>
    public MemoryBudget? MemoryBudget { get; init; }

    /// <summary>续跑起始位置（--resume 快照的 SourcePosition）：解析器按行/记录跳过已处理数据。</summary>
    public long StartFromPosition { get; init; }

    /// <summary>
    /// 宿主进度桥接（Web）：分析过程中的已处理记录数在驱动进度端口的同时转发给该桥，
    /// 使宿主无需注入进度 UI 也能收到逐批计数（API/Web 默认 null）。
    /// </summary>
    public IProgress<long>? ProgressBridge { get; init; }

    /// <summary>
    /// 数据预览捕获（Web）：分析结束后以「转换/采样后」流经的前 <see cref="PreviewLimit"/> 条记录回调一次。
    /// </summary>
    public Action<IReadOnlyList<Dictionary<string, object?>>>? PreviewSink { get; init; }

    /// <summary>预览捕获条数上限（默认 20，与 Web 现行为一致）。</summary>
    public int PreviewLimit { get; init; } = 20;

    /// <summary>分析管线生命周期观察器。用于宿主接入 Job 状态、历史记录等平台能力，Core 不依赖具体存储实现。</summary>
    public IAnalysisExecutionObserver? ExecutionObserver { get; init; }

    /// <summary>输出：本 run 实际使用的解析格式（含 valueParsers 驱动的 excel-json 切换）。</summary>
    public string? EffectiveFormat { get; internal set; }

    /// <summary>输出：本 run 已分析记录数（来自分析进度回调，供 --checkpoint 落盘）。</summary>
    public long LastRecordCount { get; internal set; }

    public long LastSourcePosition { get; internal set; }
}

/// <summary>
/// 统一分析管线的执行结果：分析结果本体 + 深度分析草图（供分布快照/漂移）。
/// </summary>
public sealed class PipelineResult
{
    public required AnalysisResult Result { get; set; }

    public AnalysisAccumulator? Accumulator { get; init; }
    public string? ExecutionSignature { get; init; }

    /// <summary>每字段 t-digest 分位草图（仅分布快照/漂移场景非空）。</summary>
    public Dictionary<string, MergeableQuantileSketch>? QuantileSketches { get; set; }

    /// <summary>每字段 HyperLogLog 基数草图（仅分布快照/漂移场景非空）。</summary>
    public Dictionary<string, HyperLogLog>? DistinctSketches { get; set; }
}
