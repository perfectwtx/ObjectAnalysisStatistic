using System.Collections.Concurrent;
using System.Text.Json;
using ObjectAnalyzer.Analysis.Options;

namespace ObjectAnalyzer.Analysis;

/// <summary>Mutable, single-owner computation state. Observe/Merge/Complete are separate operations.</summary>
public sealed class AnalysisAccumulator
{
    private static readonly JsonSerializerOptions CloneOptions = new() { MaxDepth = 512 };
    internal Dictionary<string, FieldAccumulator> Fields { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, InternalFieldMetrics> Metrics => Fields.ToDictionary(p => p.Key, p => p.Value.Components!.MetricsView(), StringComparer.Ordinal);
    internal FieldAnalyzerRegistry Analyzers { get; }
    public RuleExecutionPlan RulePlan { get; }
    internal ConcurrentDictionary<Hash128, long> ObjectCounts { get; } = new();
    internal ConcurrentDictionary<Hash128, string> ObjectSamples { get; } = new();
    internal object MergeLock { get; } = new();
    internal FieldCorrelationCollector? CorrelationCollector { get; set; }
    internal Dictionary<string, StringPatternProfiler>? StringPatterns { get; set; }
    internal Dictionary<string, UnicodeHygieneReport>? UnicodeHygiene { get; set; }
    internal AnalysisRules? Rules { get; }
    internal AdvancedAnalysisOptions Features { get; }
    internal Dictionary<string, FieldRule>? FieldRules { get; }
    internal string Configuration { get; }
    public int MaxTrackedValues { get; }
    public long TotalObjects { get; internal set; }
    internal List<ObjectRelationship> Relationships { get; } = new();
    internal Dictionary<string, long> TransformFailures { get; } = new(StringComparer.Ordinal);
    internal string? FilterExpression { get; set; }

    public AnalysisAccumulator(AnalysisRules? rules = null, AdvancedAnalysisOptions? features = null, int maxTrackedValues = 1000, FieldAnalyzerRegistry? analyzers = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxTrackedValues);
        // Freeze configuration so caller mutations cannot change an existing state.
        Rules = rules is null ? null : JsonSerializer.Deserialize<AnalysisRules>(JsonSerializer.Serialize(rules));
        Features = features is null ? new() : JsonSerializer.Deserialize<AdvancedAnalysisOptions>(JsonSerializer.Serialize(features))!;
        MaxTrackedValues = maxTrackedValues;
        Analyzers = analyzers ?? FieldAnalyzerRegistry.Default;
        RulePlan = new RuleExecutionPlan(Rules, Analyzers);
        if (Features.EnableTimeSeries && !Analyzers.Contains(BuiltInAnalyzers.DateId))
            throw new ArgumentException("Time-series analysis requires the date analyzer.");
        FieldRules = Rules?.Fields.ToDictionary(p => FieldPath.Parse(p.Key).ToString(), p => p.Value, StringComparer.Ordinal);
        Configuration = RecordValue.FromJsonElement(JsonSerializer.SerializeToElement(new { Rules, Features, MaxTrackedValues, Analyzers = Analyzers.Descriptors, Analyzers.RuleConfigurationKey })).CanonicalKey;
    }

    internal FieldAnalysisComponents CreateComponents(string path) => new(Analyzers, new FieldAnalyzerContext(path, RulePlan.Find(path)));

    public void Observe(IReadOnlyDictionary<FieldPath, RecordValue> record)
    {
        ArgumentNullException.ThrowIfNull(record);
        ObserveRange([record.ToDictionary(p => p.Key.ToString(), p => (object?)p.Value, StringComparer.Ordinal)]);
    }

    public void ObserveRange(IEnumerable<Dictionary<string, object?>> records, ParallelOptions? parallelOptions = null,
        IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        ObjectAnalyzer.Accumulate(this, records, parallelOptions, progress, cancellationToken);
    }

    public void ObserveRecords(IEnumerable<SourceRecord> records, ParallelOptions? parallelOptions = null,
        IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        ObjectAnalyzer.Accumulate(this, records.Select(record => record.ToEngineRecord()), parallelOptions, progress, cancellationToken);
    }

    public AnalysisResult Complete() => CompleteDetailed().Result;

    internal (AnalysisResult Result, Dictionary<string, MergeableQuantileSketch>? QuantileSketches,
        Dictionary<string, HyperLogLog>? DistinctSketches) CompleteDetailed()
    {
        var output = ObjectAnalyzer.Complete(this);
        output.Result.FilterExpression = FilterExpression;
        output.Result.ObjectRelationships = Clone(Relationships);
        foreach (var field in output.Result.FieldStatistics)
        {
            field.TransformFailureCount = TransformFailures.GetValueOrDefault(field.FieldName);
            field.TransformType = field.AppliedRule?.Transform?.Type;
        }
        return output;
    }

    internal static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, CloneOptions), CloneOptions)!;

    internal void CapturePipelineResult(AnalysisResult result)
    {
        FilterExpression = result.FilterExpression;
        MergeRelationships(result.ObjectRelationships ?? new());
        foreach (var field in result.FieldStatistics)
            if (field.TransformFailureCount > 0) TransformFailures[field.FieldName] = field.TransformFailureCount;
    }

    private void MergeRelationships(IEnumerable<ObjectRelationship> relationships)
    {
        foreach (var item in relationships)
        {
            var existing = Relationships.FirstOrDefault(r => r.Kind == item.Kind && r.Path == item.Path);
            if (existing is null) { Relationships.Add(Clone(item)); continue; }
            existing.OccurrenceCount += item.OccurrenceCount;
            existing.NonNullCount += item.NonNullCount;
            existing.TargetFields = existing.TargetFields.Concat(item.TargetFields).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(50).ToList();
            existing.TargetFieldCount = Math.Max(Math.Max(existing.TargetFieldCount, item.TargetFieldCount), existing.TargetFields.Count);
        }
    }

    public void Merge(AnalysisAccumulator other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (ReferenceEquals(this, other)) throw new ArgumentException("Cannot merge a state into itself.", nameof(other));
        if (Configuration != other.Configuration) throw new InvalidOperationException("Analysis configurations differ; states cannot be merged.");
        if (TotalObjects > 0 && other.TotalObjects > 0 && FilterExpression != other.FilterExpression)
            throw new InvalidOperationException("Analysis filters differ; states cannot be merged.");
        // These optional collectors do not yet have a durable merge protocol. Reject rather than lose state.
        EnsureMergeable(); other.EnsureMergeable();
        MergeRelationships(other.Relationships);
        foreach (var p in other.TransformFailures) TransformFailures[p.Key] = TransformFailures.GetValueOrDefault(p.Key) + p.Value;
        FilterExpression ??= other.FilterExpression;
        var local = new AnalyzerThreadState { TotalObjects = other.TotalObjects };
        foreach (var p in other.Fields) local.StatsMap.Add(p.Key, p.Value);
        MergeThread(local);
        foreach (var p in other.ObjectCounts) ObjectCounts.AddOrUpdate(p.Key, p.Value, (_, count) => count + p.Value);
        foreach (var p in other.ObjectSamples)
            if (ObjectSamples.Count < 10000) ObjectSamples.TryAdd(p.Key, p.Value);
    }

    internal void EnsureMergeable()
    {
        if (Features.EnableCorrelation || Features.EnableStringPatterns || Features.EnableUnicodeHygiene || !string.IsNullOrEmpty(Features.FuzzyDedupField))
            throw new NotSupportedException("Incremental state currently supports base statistics and time series; correlation, string patterns, Unicode profiling and fuzzy dedup require a complete run.");
    }

    internal void RecordObject(Hash128 hash, Func<string> sample)
    {
        long count = ObjectCounts.AddOrUpdate(hash, 1, (_, existing) => existing + 1);
        if (ObjectSamples.Count < 10000 || count > 1 && !ObjectSamples.ContainsKey(hash) && ObjectSamples.Count < 20000)
            ObjectSamples.TryAdd(hash, sample());
    }

    internal List<DuplicateObjectStatistic> BuildDuplicates() => ObjectCounts
        .Where(p => p.Value > 1 && ObjectSamples.ContainsKey(p.Key))
        .Select(p => new DuplicateObjectStatistic { Count = p.Value, ObjectJson = ObjectSamples[p.Key] })
        .OrderByDescending(p => p.Count).ThenBy(p => p.ObjectJson, StringComparer.Ordinal).Take(10000).ToList();

    internal void MergeThread(AnalyzerThreadState local)
    {
        lock (MergeLock)
        {
            TotalObjects += local.TotalObjects;

            foreach (var localStat in local.StatsMap.Values)
            {
                string fName = localStat.FieldName;
                if (!Fields.TryGetValue(fName, out var globalStat))
                {
                    globalStat = new FieldAccumulator { FieldName = fName, Components = CreateComponents(fName) };
                    Fields[fName] = globalStat;
                }

                globalStat.Components!.Merge(localStat.Components!);
                globalStat.Count += localStat.Count;
                globalStat.ValueCountsTruncated |= localStat.ValueCountsTruncated;
                globalStat.OtherValueCount += localStat.OtherValueCount;
                globalStat.NumbersAreIntegral &= localStat.NumbersAreIntegral;
                globalStat.NumbersFitInt64 &= localStat.NumbersFitInt64;
                globalStat.NumbersFitDecimal &= localStat.NumbersFitDecimal;
                globalStat.NumbersFitDouble &= localStat.NumbersFitDouble;

                foreach (var tc in localStat.TypeCounts)
                {
                    globalStat.TypeCounts[tc.Key] = globalStat.TypeCounts.GetValueOrDefault(tc.Key) + tc.Value;
                }
                globalStat.NullCount += localStat.NullCount;

                foreach (var vc in localStat.ValueCounts)
                {
                    globalStat.AddValue(vc.Key, vc.Value, MaxTrackedValues);
                }
            }

            // 深度分析 增强：合并线程局部收集器
            // 相关性：每个线程的收集器持有该线程处理的对象的"行样本"切片；
            // 把所有线程的收集器按"对象顺序"追加合并，保证 Pearson/Spearman 计算使用全量数据。
            if (local.CorrelationCollector != null)
            {
                if (CorrelationCollector == null)
                {
                    CorrelationCollector = local.CorrelationCollector;
                }
                else if (!ReferenceEquals(local.CorrelationCollector, CorrelationCollector))
                {
                    CorrelationCollector.MergeFrom(local.CorrelationCollector);
                }
            }

            // 字符串模式：合并到 global（按字段求和；不同线程的 patterns 互不重叠，累加即可）
            if (local.StringPatterns != null)
            {
                if (StringPatterns == null)
                {
                    StringPatterns = new Dictionary<string, StringPatternProfiler>(StringComparer.Ordinal);
                }
                foreach (var (field, profiler) in local.StringPatterns)
                {
                    if (!StringPatterns.TryGetValue(field, out var dest))
                    {
                        // 复制一份避免线程间共享底层数据；StringPatternProfiler 内部只持有不可变 dict / list
                        dest = new StringPatternProfiler();
                        StringPatterns[field] = dest;
                    }
                    dest.MergeFrom(profiler);
                }
            }

            // Unicode 卫生：每线程的累加直接 MergeInto 到 global
            if (local.UnicodeHygiene != null)
            {
                if (UnicodeHygiene == null)
                {
                    UnicodeHygiene = new Dictionary<string, UnicodeHygieneReport>(StringComparer.Ordinal);
                }
                foreach (var (field, report) in local.UnicodeHygiene)
                {
                    if (!UnicodeHygiene.TryGetValue(field, out var dest))
                    {
                        dest = new UnicodeHygieneReport();
                        UnicodeHygiene[field] = dest;
                    }
                    UnicodeHygieneChecker.MergeInto(dest, report);
                }
            }
        }
    }

    public AnalysisStateSnapshot Capture()
    {
        EnsureMergeable();
        return new AnalysisStateSnapshot
        {
            Rules = Clone(Rules), Features = Clone(Features), MaxTrackedValues = MaxTrackedValues, TotalObjects = TotalObjects,
            Relationships = Clone(Relationships), TransformFailures = new(TransformFailures), FilterExpression = FilterExpression,
            Analyzers = Analyzers.Descriptors.ToList(), RuleConfigurationKey = Analyzers.RuleConfigurationKey,
            Fields = Fields.Values.Select(f => new FieldStateSnapshot { Counters = f.Capture(), Components = f.Components!.Capture() }).ToList(),
            Objects = ObjectCounts.Select(p => new ObjectStateSnapshot { High = p.Key.High, Low = p.Key.Low, Count = p.Value,
                Sample = ObjectSamples.GetValueOrDefault(p.Key) }).ToList()
        };
    }
}
