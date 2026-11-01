using ObjectAnalyzer.Analysis.Options;

namespace ObjectAnalyzer.Analysis;

public static partial class ObjectAnalyzer
{
    private const int MaxTrackedUniqueValues = 1000;

    /// <summary>Typed entry point. Literal property names and structural paths remain unambiguous.</summary>
    public static AnalysisResult AnalyzeRecords(
        IEnumerable<IReadOnlyDictionary<FieldPath, RecordValue>> records,
        AnalysisRules? rules = null,
        ParallelOptions? parallelOptions = null,
        AdvancedAnalysisOptions? features = null,
        IProgress<long>? progress = null,
        int? maxTrackedValues = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        return Analyze(records.Select(record => record.ToDictionary(
            p => p.Key.ToString(), p => (object?)p.Value, StringComparer.Ordinal)),
            rules, parallelOptions, features, progress, maxTrackedValues, cancellationToken);
    }

    /// <summary>
    /// 主流分析入口：返回 <see cref="AnalysisResult"/>，不暴露内部草图。
    /// 需要每字段的 t-digest / HyperLogLog 草图（用于分布快照 / 漂移）时使用 <see cref="AnalyzeDetailed"/>。
    /// </summary>
    public static AnalysisResult Analyze(
        IEnumerable<Dictionary<string, object?>> objects,
        AnalysisRules? rules = null,
        ParallelOptions? parallelOptions = null,
        AdvancedAnalysisOptions? features = null,
        IProgress<long>? progress = null,
        int? maxTrackedValues = null,
        CancellationToken cancellationToken = default)
    {
        return AnalyzeCore(objects, rules, parallelOptions, features, cancellationToken, progress, maxTrackedValues).Result;
    }

    /// <summary>
    /// 深度分析 增强入口：在返回 <see cref="AnalysisResult"/> 的同时，暴露每字段的 t-digest 草图与 HyperLogLog 草图，
    /// 供 <c>--export-distribution-snapshot</c> / <c>--diff-distribution</c> 做分布快照与漂移检测。
    /// 调用方无需草图时使用 <see cref="Analyze"/> 即可。
    /// </summary>
    public static (
        AnalysisResult Result,
        Dictionary<string, MergeableQuantileSketch>? QuantileSketches,
        Dictionary<string, HyperLogLog>? DistinctSketches
    ) AnalyzeDetailed(
        IEnumerable<Dictionary<string, object?>> objects,
        AnalysisRules? rules = null,
        ParallelOptions? parallelOptions = null,
        AdvancedAnalysisOptions? features = null,
        IProgress<long>? progress = null,
        int? maxTrackedValues = null,
        CancellationToken cancellationToken = default)
    {
        return AnalyzeCore(objects, rules, parallelOptions, features, cancellationToken, progress, maxTrackedValues);
    }

    /// <summary>
    /// 实际分析主流程：所有并行累计、合并、后置处理均在此实现。
    /// 返回元组 (Result, QuantileSketches?, DistinctSketches?)：草图为 null 表示对应类型无数据。
    /// </summary>
    private static (
        AnalysisResult Result,
        Dictionary<string, MergeableQuantileSketch>? QuantileSketches,
        Dictionary<string, HyperLogLog>? DistinctSketches
    ) AnalyzeCore(
        IEnumerable<Dictionary<string, object?>> objects,
        AnalysisRules? rules,
        ParallelOptions? parallelOptions,
        AdvancedAnalysisOptions? features,
        CancellationToken cancellationToken,
        IProgress<long>? progress = null,
        int? maxTrackedValues = null)
    {
        var state = new AnalysisAccumulator(rules, features, maxTrackedValues ?? MaxTrackedUniqueValues);
        state.ObserveRange(objects, parallelOptions, progress, cancellationToken);
        return state.CompleteDetailed();
    }
}
