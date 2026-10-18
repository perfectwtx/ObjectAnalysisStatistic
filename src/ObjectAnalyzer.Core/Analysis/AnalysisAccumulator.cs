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
    public AnalysisResourceLimits Limits { get; }
    internal RetainedPayloadBudget Payload { get; }
    private readonly HashSet<string> _knownFields = new(StringComparer.Ordinal);
    private readonly object _objectLock = new();
    private Exception? _failure;
    private void EnsureValid()
    { if (_failure is not null) throw new InvalidOperationException("Analysis state is incomplete after a failed observation or merge; create a new state.", _failure); }
    public int MaxTrackedValues { get; }
    public long TotalObjects { get; internal set; }
    internal List<ObjectRelationship> Relationships { get; } = new();
    internal Dictionary<string, long> TransformFailures { get; } = new(StringComparer.Ordinal);
    internal string? FilterExpression { get; set; }

    public AnalysisAccumulator(AnalysisRules? rules = null, AdvancedAnalysisOptions? features = null, int maxTrackedValues = 1000, FieldAnalyzerRegistry? analyzers = null, AnalysisResourceLimits? resourceLimits = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxTrackedValues);
        // Freeze configuration so caller mutations cannot change an existing state.
        Rules = rules is null ? null : JsonSerializer.Deserialize<AnalysisRules>(JsonSerializer.Serialize(rules));
        Features = features is null ? new() : JsonSerializer.Deserialize<AdvancedAnalysisOptions>(JsonSerializer.Serialize(features))!;
        Limits = resourceLimits ?? new();
        Limits.Validate();
        Payload = new(Limits.MaxRetainedPayloadBytes);
        MaxTrackedValues = maxTrackedValues;
        Analyzers = analyzers ?? FieldAnalyzerRegistry.Default;
        if (Analyzers.Factories.Length > Limits.MaxAnalyzerComponents) throw new AnalysisResourceLimitException("analyzer-components", Limits.MaxAnalyzerComponents);
        if (Rules?.Fields.Count > Limits.MaxFields) throw new AnalysisResourceLimitException("rule-fields", Limits.MaxFields);
        if (Rules?.Fields.Values.Any(rule => (rule.Checks?.Count ?? 0) > Limits.MaxRulesPerField) == true) throw new AnalysisResourceLimitException("rules-per-field", Limits.MaxRulesPerField);
        RulePlan = new RuleExecutionPlan(Rules, Analyzers);
        if (Features.EnableCorrelation && Features.CorrelationReservoir < 1)
            throw new ArgumentException("Correlation reservoir must be positive.");
        foreach (var path in Rules?.Fields.Keys ?? Enumerable.Empty<string>())
        {
            string canonical = FieldPath.Parse(path).ToString(); RegisterField(canonical);
            if (RulePlan.Find(canonical)?.Checks.Count > Limits.MaxRulesPerField)
                throw new AnalysisResourceLimitException("rules-per-field", Limits.MaxRulesPerField);
        }
        if (Features.EnableTimeSeries && !Analyzers.Contains(BuiltInAnalyzers.DateId))
            throw new ArgumentException("Time-series analysis requires the date analyzer.");
        FieldRules = Rules?.Fields.ToDictionary(p => FieldPath.Parse(p.Key).ToString(), p => p.Value, StringComparer.Ordinal);
        Configuration = RecordValue.FromJsonElement(JsonSerializer.SerializeToElement(new { Rules, Features, MaxTrackedValues, Limits, Analyzers = Analyzers.Descriptors, Analyzers.RuleConfigurationKey })).CanonicalKey;
    }

    internal FieldAnalysisComponents CreateComponents(string path, bool global = true) => new(Analyzers, new FieldAnalyzerContext(path, RulePlan.Find(path), Limits, global ? Payload : null));

    internal void RegisterField(string path)
    {
        lock (MergeLock)
        {
            if (path.Length > Limits.MaxPathCharacters) throw new AnalysisResourceLimitException("path-characters", Limits.MaxPathCharacters);
            if (_knownFields.Contains(path)) return;
            if (_knownFields.Count >= Limits.MaxFields) throw new AnalysisResourceLimitException("fields", Limits.MaxFields);
            Payload.Reserve(path.Length * 2L + 64);
            _knownFields.Add(path);
        }
    }

    public void Observe(IReadOnlyDictionary<FieldPath, RecordValue> record)
    {
        ArgumentNullException.ThrowIfNull(record);
        ObserveRange([record.ToDictionary(p => p.Key.ToString(), p => (object?)p.Value, StringComparer.Ordinal)]);
    }

    public void ObserveRange(IEnumerable<Dictionary<string, object?>> records, ParallelOptions? parallelOptions = null,
        IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        EnsureValid();
        try { ObjectAnalyzer.Accumulate(this, records, parallelOptions, progress, cancellationToken); }
        catch (Exception exception) { _failure = exception; throw; }
    }

    public void ObserveRecords(IEnumerable<SourceRecord> records, ParallelOptions? parallelOptions = null,
        IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        ObserveRange(records.Select(record => record.ToEngineRecord()), parallelOptions, progress, cancellationToken);
    }

    public AnalysisResult Complete() => CompleteDetailed().Result;

    internal (AnalysisResult Result, Dictionary<string, MergeableQuantileSketch>? QuantileSketches,
        Dictionary<string, HyperLogLog>? DistinctSketches) CompleteDetailed()
    {
        EnsureValid();
        var output = ObjectAnalyzer.Complete(this);
        output.Result.Reliability = BuildReliability(output.Result);
        output.Result.PrivacyDirectives = PiiPolicy.Directives(Rules);
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

    internal void MergeRelationships(IEnumerable<ObjectRelationship> relationships)
    {
        foreach (var item in relationships)
        {
            if (item.Path.Length > Limits.MaxPathCharacters) throw new AnalysisResourceLimitException("relationship-path-characters", Limits.MaxPathCharacters);
            var existing = Relationships.FirstOrDefault(r => r.Kind == item.Kind && r.Path == item.Path);
            if (existing is null)
            {
                if (Relationships.Count >= Limits.MaxRelationshipPaths) throw new AnalysisResourceLimitException("relationship-paths", Limits.MaxRelationshipPaths);
                Relationships.Add(Clone(item)); continue;
            }
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
        EnsureValid(); other.EnsureValid();
        if (Configuration != other.Configuration) throw new InvalidOperationException("Analysis configurations differ; states cannot be merged.");
        if (TotalObjects > 0 && other.TotalObjects > 0 && FilterExpression != other.FilterExpression)
            throw new InvalidOperationException("Analysis filters differ; states cannot be merged.");
        // These optional collectors do not yet have a durable merge protocol. Reject rather than lose state.
        EnsureMergeable(); other.EnsureMergeable();
        try
        {
            MergeRelationships(other.Relationships);
            foreach (var p in other.TransformFailures) TransformFailures[p.Key] = TransformFailures.GetValueOrDefault(p.Key) + p.Value;
            FilterExpression ??= other.FilterExpression;
            var local = new AnalyzerThreadState { TotalObjects = other.TotalObjects };
            foreach (var p in other.Fields) local.StatsMap.Add(p.Key, p.Value);
            MergeThread(local);
            foreach (var p in other.ObjectCounts)
            {
                if (!ObjectCounts.ContainsKey(p.Key))
                {
                    if (ObjectCounts.Count >= Limits.MaxObjectIdentities) throw new AnalysisResourceLimitException("object-identities", Limits.MaxObjectIdentities);
                    Payload.Reserve(64);
                }
                ObjectCounts.AddOrUpdate(p.Key, p.Value, (_, count) => count + p.Value);
            }
            foreach (var p in other.ObjectSamples)
                if (ObjectSamples.Count < Limits.MaxObjectSamples && !ObjectSamples.ContainsKey(p.Key))
                { Payload.Reserve(p.Value.Length * 2L); ObjectSamples.TryAdd(p.Key, p.Value); }
        }

        catch (Exception exception) { _failure = exception; throw; }
    }

    internal void EnsureMergeable()
    {
        if (Features.EnableCorrelation || Features.EnableStringPatterns || Features.EnableUnicodeHygiene || !string.IsNullOrEmpty(Features.FuzzyDedupField))
            throw new NotSupportedException("Incremental state currently supports base statistics and time series; correlation, string patterns, Unicode profiling and fuzzy dedup require a complete run.");
    }

    internal void RecordObject(Hash128 hash, Func<string> sample)
    {
        lock (_objectLock)
        {
            bool found = ObjectCounts.TryGetValue(hash, out long count);
            if (!found)
            {
                if (ObjectCounts.Count >= Limits.MaxObjectIdentities) throw new AnalysisResourceLimitException("object-identities", Limits.MaxObjectIdentities);
                Payload.Reserve(64);
            }
            ObjectCounts[hash] = count + 1;
            if (ObjectSamples.ContainsKey(hash) || ObjectSamples.Count >= Limits.MaxObjectSamples) return;
            string text = sample();
            if (text.Length * 2L > Limits.MaxSampleValueBytes) return;
            Payload.Reserve(text.Length * 2L);
            ObjectSamples.TryAdd(hash, text);
        }
    }

    internal AnalysisReliability BuildReliability(AnalysisResult result)
    {
        var metadata = new AnalysisReliability();
        if (result.FieldStatistics.Any(field => field.ValueCountsTruncated))
            metadata.Limitations.Add(new("field-frequencies", AnalysisAccuracy.Truncated, "Retained identities/value size are bounded; OtherValueCount covers omitted occurrences."));
        if (result.FieldStatistics.Any(field => field.DistinctApproximate))
            metadata.Limitations.Add(new("field-distinct", AnalysisAccuracy.Approximate, "HyperLogLog estimate after frequency truncation."));
        if (result.FieldStatistics.Any(field => field.MedianNumber.HasValue))
            metadata.Limitations.Add(new("quantiles", AnalysisAccuracy.Approximate, "Bounded mergeable quantile sketch."));
        if (result.TotalDuplicateGroups > result.DuplicateObjects.Count)
            metadata.Limitations.Add(new("duplicate-examples", AnalysisAccuracy.Truncated, "Counts remain exact; only bounded, size-limited examples are retained."));
        if (result.FieldStatistics.Any(field => field.ValueRuleResults?.Values.Any(rule => rule.SamplesTruncated) == true))
            metadata.Limitations.Add(new("rule-examples", AnalysisAccuracy.Truncated, "Violation counts cover all records; raw examples are bounded."));
        if (result.FieldStatistics.Any(field => field.ValueRuleResults?.Values.Any(rule => rule.DistinctApproximate) == true))
            metadata.Limitations.Add(new("rule-distinct", AnalysisAccuracy.Approximate, "HyperLogLog replaces exact distinct identities after their retention threshold."));
        if (CorrelationCollector is { } correlation && correlation.Seen > correlation.SampleCount)
            metadata.Limitations.Add(new("correlation", AnalysisAccuracy.Sampled, "A shared row reservoir preserves column alignment."));
        if (StringPatterns?.Values.Any(profiler => profiler.ExamplesTruncated) == true)
            metadata.Limitations.Add(new("pattern-examples", AnalysisAccuracy.Truncated, "Template counts cover all strings; examples are shortened."));
        return metadata;
    }

    internal List<DuplicateObjectStatistic> BuildDuplicates() => ObjectCounts
        .Where(p => p.Value > 1 && ObjectSamples.ContainsKey(p.Key))
        .Select(p => new DuplicateObjectStatistic { Count = p.Value, ObjectJson = ObjectSamples[p.Key] })
        .OrderByDescending(p => p.Count).ThenBy(p => p.ObjectJson, StringComparer.Ordinal).Take(Limits.MaxObjectSamples).ToList();

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
                    RegisterField(fName);
                    globalStat = new FieldAccumulator(Limits, Payload) { FieldName = fName, Components = CreateComponents(fName) };
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

            // Merge bounded whole-row reservoirs by priority, preserving column alignment.
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
                        dest = new StringPatternProfiler(Limits, Payload);
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
        EnsureValid();
        EnsureMergeable();
        return new AnalysisStateSnapshot
        {
            Rules = Clone(Rules), Features = Clone(Features), ResourceLimits = Limits, MaxTrackedValues = MaxTrackedValues, TotalObjects = TotalObjects,
            Relationships = Clone(Relationships), TransformFailures = new(TransformFailures), FilterExpression = FilterExpression,
            Analyzers = Analyzers.Descriptors.ToList(), RuleConfigurationKey = Analyzers.RuleConfigurationKey,
            Fields = Fields.Values.Select(f => new FieldStateSnapshot { Counters = f.Capture(), Components = f.Components!.Capture() }).ToList(),
            Objects = ObjectCounts.Select(p => new ObjectStateSnapshot { High = p.Key.High, Low = p.Key.Low, Count = p.Value,
                Sample = ObjectSamples.GetValueOrDefault(p.Key) }).ToList()
        };
    }
}
