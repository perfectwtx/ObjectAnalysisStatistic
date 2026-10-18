namespace ObjectAnalyzer.Analysis;

public sealed record ValueRuleResult
{
    public bool SamplesTruncated { get; init; }
    public long Count { get; init; }
    public long DistinctValueCount { get; init; }
    public bool DistinctApproximate { get; init; }
    public string Message { get; init; } = "";
    public IReadOnlyList<string> Samples { get; init; } = Array.Empty<string>();
}

internal sealed class ValueRuleAccumulator(AnalysisResourceLimits? limits = null, RetainedPayloadBudget? payload = null)
{
    private readonly AnalysisResourceLimits _limits = limits ?? new();
    private readonly RetainedPayloadBudget? _payload = payload;
    private bool _samplesTruncated;
    private const int ExactDistinctLimit = 128;
    private readonly HashSet<RecordValue> _exact = new();
    private HyperLogLog? _distinct;
    private readonly SortedSet<RecordValue> _samples = new();
    public long Count { get; private set; }

    public void Add(RecordValue value)
    {
        Count++;
        AddDistinct(value);
        AddSample(value);
    }

    private void AddSample(RecordValue value)
    {
        if (_samples.Contains(value)) return;
        int maximum = Math.Min(QualityChecks.MaxSamples, _limits.MaxRuleSamples);
        if (maximum == 0 || RetainedPayloadBudget.Size(value) > _limits.MaxSampleValueBytes)
        { _samplesTruncated = true; return; }
        if (_samples.Count == maximum)
        {
            _samplesTruncated = true;
            var largest = _samples.Max!;
            if (value.CompareTo(largest) >= 0) return;
            long delta = RetainedPayloadBudget.Size(value) - RetainedPayloadBudget.Size(largest);
            if (delta > 0) _payload?.Reserve(delta); else _payload?.Release(-delta);
            _samples.Remove(largest);
        }
        else _payload?.Reserve(RetainedPayloadBudget.Size(value));
        _samples.Add(value);
    }

    private void PromoteDistinct()
    {
        _distinct = new();
        foreach (var item in _exact) { _distinct.Add(item.CanonicalKey); _payload?.Release(RetainedPayloadBudget.Size(item)); }
        _exact.Clear();
    }

    private void AddDistinct(RecordValue value)
    {
        if (_distinct is not null) { _distinct.Add(value.CanonicalKey); return; }
        if (RetainedPayloadBudget.Size(value) > _limits.MaxSampleValueBytes) { PromoteDistinct(); _distinct!.Add(value.CanonicalKey); return; }
        if (_exact.Contains(value)) return;
        if (_exact.Count == ExactDistinctLimit) { PromoteDistinct(); _distinct!.Add(value.CanonicalKey); return; }
        _payload?.Reserve(RetainedPayloadBudget.Size(value));
        _exact.Add(value);
    }

    public void Merge(ValueRuleAccumulator other)
    {
        Count += other.Count;
        _samplesTruncated |= other._samplesTruncated;
        if (other._distinct is not null)
        {
            if (_distinct is null)
            {
                PromoteDistinct();
            }
            _distinct!.Merge(other._distinct);
        }
        else foreach (var item in other._exact) AddDistinct(item);
        foreach (var item in other._samples) AddSample(item);
    }

    public ValueRuleResult Complete(string message) => new()
    {
        SamplesTruncated = _samplesTruncated, Count = Count, DistinctValueCount = _distinct?.Estimate() ?? _exact.Count,
        DistinctApproximate = _distinct is not null, Message = message,
        Samples = _samples.Select(v => v.ToDisplayString()).ToArray()
    };

    public ValueRuleStateSnapshot Capture() => new()
    {
        SamplesTruncated = _samplesTruncated, Count = Count, Exact = _exact.ToList(), Distinct = _distinct?.Capture(), Samples = _samples.ToList()
    };

    public static ValueRuleAccumulator Restore(ValueRuleStateSnapshot data, AnalysisResourceLimits? limits = null, RetainedPayloadBudget? payload = null)
    {
        if (data.Count < 0 || data.Samples.Count > QualityChecks.MaxSamples || data.Exact.Count > ExactDistinctLimit
            || data.Distinct is not null && data.Exact.Count > 0 || data.Exact.Count > data.Count
            || data.Exact.Any(v => v is null) || data.Samples.Any(v => v is null)
            || data.Exact.Distinct().Count() != data.Exact.Count || data.Samples.Distinct().Count() != data.Samples.Count
            || data.Count == 0 && (data.Samples.Count > 0 || data.Exact.Count > 0 || data.Distinct is not null)
            || data.Count > 0 && (data.Samples.Count == 0 && !data.SamplesTruncated || data.Distinct is null && data.Exact.Count == 0)
            || data.Distinct is null && data.Samples.Any(v => !data.Exact.Contains(v)))
            throw new InvalidDataException("Invalid value-rule state.");
        var state = new ValueRuleAccumulator(limits, payload) { _samplesTruncated = data.SamplesTruncated, Count = data.Count, _distinct = data.Distinct is null ? null : HyperLogLog.Restore(data.Distinct) };
        state._exact.UnionWith(data.Exact);
        state._samples.UnionWith(data.Samples);
        if (state._samples.Count > Math.Min(QualityChecks.MaxSamples, state._limits.MaxRuleSamples) || state._samples.Concat(state._exact).Any(value => RetainedPayloadBudget.Size(value) > state._limits.MaxSampleValueBytes))
            throw new InvalidDataException("Rule examples exceed their retention budget.");
        foreach (var value in state._samples.Concat(state._exact)) payload?.Reserve(RetainedPayloadBudget.Size(value));
        return state;
    }
}

public sealed class ValueRuleStateSnapshot
{
    public bool SamplesTruncated { get; set; }
    public long Count { get; set; }
    public List<RecordValue> Exact { get; set; } = new();
    public HyperLogLogStateSnapshot? Distinct { get; set; }
    public List<RecordValue> Samples { get; set; } = new();
}
