namespace ObjectAnalyzer.Analysis;

public sealed record ValueRuleResult
{
    public long Count { get; init; }
    public long DistinctValueCount { get; init; }
    public bool DistinctApproximate { get; init; }
    public string Message { get; init; } = "";
    public IReadOnlyList<string> Samples { get; init; } = Array.Empty<string>();
}

internal sealed class ValueRuleAccumulator
{
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
        _samples.Add(value);
        if (_samples.Count > QualityChecks.MaxSamples) _samples.Remove(_samples.Max!);
    }

    private void AddDistinct(RecordValue value)
    {
        if (_distinct is not null) { _distinct.Add(value.CanonicalKey); return; }
        _exact.Add(value);
        if (_exact.Count <= ExactDistinctLimit) return;
        _distinct = new();
        foreach (var item in _exact) _distinct.Add(item.CanonicalKey);
        _exact.Clear();
    }

    public void Merge(ValueRuleAccumulator other)
    {
        Count += other.Count;
        if (other._distinct is not null)
        {
            if (_distinct is null)
            {
                _distinct = new();
                foreach (var item in _exact) _distinct.Add(item.CanonicalKey);
                _exact.Clear();
            }
            _distinct.Merge(other._distinct);
        }
        else foreach (var item in other._exact) AddDistinct(item);
        foreach (var item in other._samples) AddSample(item);
    }

    public ValueRuleResult Complete(string message) => new()
    {
        Count = Count, DistinctValueCount = _distinct?.Estimate() ?? _exact.Count,
        DistinctApproximate = _distinct is not null, Message = message,
        Samples = _samples.Select(v => v.ToDisplayString()).ToArray()
    };

    public ValueRuleStateSnapshot Capture() => new()
    {
        Count = Count, Exact = _exact.ToList(), Distinct = _distinct?.Capture(), Samples = _samples.ToList()
    };

    public static ValueRuleAccumulator Restore(ValueRuleStateSnapshot data)
    {
        if (data.Count < 0 || data.Samples.Count > QualityChecks.MaxSamples || data.Exact.Count > ExactDistinctLimit
            || data.Distinct is not null && data.Exact.Count > 0 || data.Exact.Count > data.Count
            || data.Exact.Any(v => v is null) || data.Samples.Any(v => v is null)
            || data.Exact.Distinct().Count() != data.Exact.Count || data.Samples.Distinct().Count() != data.Samples.Count
            || data.Count == 0 && (data.Samples.Count > 0 || data.Exact.Count > 0 || data.Distinct is not null)
            || data.Count > 0 && (data.Samples.Count == 0 || data.Distinct is null && data.Exact.Count == 0)
            || data.Distinct is null && data.Samples.Any(v => !data.Exact.Contains(v)))
            throw new InvalidDataException("Invalid value-rule state.");
        var state = new ValueRuleAccumulator { Count = data.Count, _distinct = data.Distinct is null ? null : HyperLogLog.Restore(data.Distinct) };
        state._exact.UnionWith(data.Exact);
        state._samples.UnionWith(data.Samples);
        return state;
    }
}

public sealed class ValueRuleStateSnapshot
{
    public long Count { get; set; }
    public List<RecordValue> Exact { get; set; } = new();
    public HyperLogLogStateSnapshot? Distinct { get; set; }
    public List<RecordValue> Samples { get; set; } = new();
}
