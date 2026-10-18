using ObjectAnalyzer.Analysis.Options;

namespace ObjectAnalyzer.Analysis;

/// <summary>Durable accumulator data, distinct from user-facing AnalysisResult.</summary>
public sealed class AnalysisStateSnapshot
{
    public string Version { get; set; } = "analysis-state/v3";
    public AnalysisResourceLimits ResourceLimits { get; set; } = new();
    public AnalysisRules? Rules { get; set; }
    public AdvancedAnalysisOptions Features { get; set; } = new();
    public List<AnalyzerDescriptor> Analyzers { get; set; } = FieldAnalyzerRegistry.Default.Descriptors.ToList();
    public string RuleConfigurationKey { get; set; } = FieldAnalyzerRegistry.Default.RuleConfigurationKey;
    public int MaxTrackedValues { get; set; } = 1000;
    public long TotalObjects { get; set; }
    public List<FieldStateSnapshot> Fields { get; set; } = new();
    public List<ObjectStateSnapshot> Objects { get; set; } = new();
    public List<ObjectRelationship> Relationships { get; set; } = new();
    public Dictionary<string, long> TransformFailures { get; set; } = new();
    public string? FilterExpression { get; set; }

    public AnalysisAccumulator Restore(FieldAnalyzerRegistry? analyzers = null)
    {
        if (Version != "analysis-state/v3") throw new InvalidDataException("Unsupported analysis state version. Re-analyze the source.");
        if (TotalObjects < 0) throw new InvalidDataException("Negative record count.");
        var registry = (analyzers ?? FieldAnalyzerRegistry.Default).Resolve(Analyzers);
        if (RuleConfigurationKey != registry.RuleConfigurationKey)
            throw new InvalidDataException("Rule compiler registrations or configurations do not match the snapshot.");
        var state = new AnalysisAccumulator(Rules, Features, MaxTrackedValues, registry, ResourceLimits) { TotalObjects = TotalObjects };
        state.EnsureMergeable();
        state.MergeRelationships(Relationships);
        foreach (var p in TransformFailures)
        {
            state.RegisterField(p.Key);
            if (p.Value < 0) throw new InvalidDataException("Negative transformation failure count.");
            state.TransformFailures.Add(p.Key, p.Value);
        }
        state.FilterExpression = FilterExpression;
        foreach (var field in Fields)
        {
            state.RegisterField(field.Counters.FieldName);
            var counters = field.Counters;
            var components = state.CreateComponents(field.Counters.FieldName);
            components.Restore(field.Components);
            components.ValidateObservedKinds(field.Counters.TypeCounts.Keys);
            var metrics = components.MetricsView().Capture();
            if (counters.Count < 0 || counters.Count > TotalObjects || counters.NullCount < 0 || counters.NullCount > counters.Count
                || counters.TypeCounts.Values.Any(v => v <= 0) || counters.TypeCounts.Values.Sum() != counters.Count
                || counters.ValueCounts.Values.Any(v => v <= 0) || counters.OtherValueCount < 0
                || counters.ValueCounts.Values.Sum() + counters.OtherValueCount != counters.Count
                || counters.ValueCounts.Count > MaxTrackedValues || counters.TypeCounts.GetValueOrDefault("Null") != counters.NullCount
                || counters.OtherValueCount > 0 && !counters.ValueCountsTruncated)
                throw new InvalidDataException("Invalid field count.");
            if (metrics.NumberCount < 0 || metrics.NumberCount > counters.Count || metrics.StringCount < 0 || metrics.StringCount > counters.Count
                || metrics.DateTimeCount < 0 || metrics.DateTimeCount > counters.Count || metrics.LengthSum < 0
                || metrics.Quantiles?.Centroids.Sum(c => c.Weight) != (metrics.NumberCount > 0 ? metrics.NumberCount : null)
                || metrics.ZeroCount < 0 || (metrics.Histogram?.Values.Sum() ?? 0) + metrics.ZeroCount != metrics.NumberCount
                || (metrics.DateHistogram?.Values.Sum() ?? 0) != metrics.DateTimeCount
                || counters.Count > 0 && registry.Contains(BuiltInAnalyzers.DistinctId) && metrics.DistinctHll is null)
                throw new InvalidDataException("Invalid field metric state.");
            string name = FieldPath.Parse(field.Counters.FieldName).ToString();
            var restoredCounters = FieldAccumulator.Restore(field.Counters, state.Limits, state.Payload);
            restoredCounters.FieldName = name;
            restoredCounters.Components = components;
            state.Fields.Add(name, restoredCounters);
        }
        if (Objects.Count > ResourceLimits.MaxObjectIdentities) throw new AnalysisResourceLimitException("object-identities", ResourceLimits.MaxObjectIdentities);
        if (Objects.Count(item => item.Sample is not null) > ResourceLimits.MaxObjectSamples) throw new AnalysisResourceLimitException("object-samples", ResourceLimits.MaxObjectSamples);
        long count = 0;
        foreach (var item in Objects)
        {
            if (item.Count <= 0) throw new InvalidDataException("Invalid object occurrence count.");
            count = checked(count + item.Count);
            var hash = new Hash128(item.High, item.Low);
            if (!state.ObjectCounts.TryAdd(hash, item.Count)) throw new InvalidDataException("Duplicate object hash.");
            state.Payload.Reserve(64);
            if (item.Sample is not null)
            {
                if (item.Sample.Length * 2L > ResourceLimits.MaxSampleValueBytes) throw new InvalidDataException("Object example exceeds its size budget.");
                state.Payload.Reserve(item.Sample.Length * 2L);
                state.ObjectSamples.TryAdd(hash, item.Sample);
            }
        }
        if (count != TotalObjects) throw new InvalidDataException("Object state does not match total record count.");
        return state;
    }
}

public sealed class FieldStateSnapshot
{
    public FieldCountersSnapshot Counters { get; set; } = new();
    public List<FieldAnalyzerStateSnapshot> Components { get; set; } = new();

    /// <summary>Compatibility diagnostic projection, not a second persisted source of truth.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public FieldMetricsSnapshot Metrics
    {
        get
        {
            var metrics = new InternalFieldMetrics(int.MaxValue);
            foreach (var component in Components.Where(c => BuiltInAnalyzers.MetricIds.Contains(c.Id)))
                metrics.MergeFrom(InternalFieldMetrics.Restore(System.Text.Json.JsonSerializer.Deserialize<FieldMetricsSnapshot>(component.Data)!, int.MaxValue));
            return metrics.Capture();
        }
    }
}

public sealed class ObjectStateSnapshot
{
    public ulong High { get; set; }
    public ulong Low { get; set; }
    public long Count { get; set; }
    public string? Sample { get; set; }
}

public sealed class HyperLogLogStateSnapshot
{
    public int Precision { get; set; }
    public Dictionary<int, byte> Registers { get; set; } = new();
}

public sealed class QuantileStateSnapshot
{
    public int Capacity { get; set; }
    public List<QuantileCentroid> Centroids { get; set; } = new();
}
