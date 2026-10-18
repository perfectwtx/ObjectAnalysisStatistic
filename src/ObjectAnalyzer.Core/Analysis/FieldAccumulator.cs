namespace ObjectAnalyzer.Analysis;

/// <summary>Observed field counters. Derived report values never participate in merging.</summary>
internal sealed class FieldAccumulator(AnalysisResourceLimits? limits = null, RetainedPayloadBudget? payload = null)
{
    private readonly AnalysisResourceLimits _limits = limits ?? new();
    private readonly RetainedPayloadBudget? _payload = payload;
    internal FieldAnalysisComponents? Components { get; set; }

    public string FieldName { get; set; } = "";
    public long Count { get; set; }
    public long NullCount { get; set; }
    public Dictionary<string, long> TypeCounts { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<RecordValue, long> ValueCounts { get; set; } = new();
    public long OtherValueCount { get; set; }
    public bool ValueCountsTruncated { get; set; }
    public bool NumbersAreIntegral { get; set; } = true;
    public bool NumbersFitInt64 { get; set; } = true;
    public bool NumbersFitDecimal { get; set; } = true;
    public bool NumbersFitDouble { get; set; } = true;

    public void AddValue(RecordValue value, long count, int limit)
    {
        if (RetainedPayloadBudget.Size(value) > _limits.MaxFrequencyValueBytes)
        { ValueCountsTruncated = true; OtherValueCount += count; return; }
        if (ValueCounts.TryGetValue(value, out long current)) { ValueCounts[value] = current + count; return; }
        if (ValueCounts.Count < limit) { _payload?.Reserve(RetainedPayloadBudget.Size(value)); ValueCounts.Add(value, count); return; }
        ValueCountsTruncated = true;
        // Keep the smallest identities. The threshold only decreases, allowing exact counts
        // for retained values across arbitrary partitions without keeping all identities.
        if (limit > 0)
        {
            RecordValue largest = ValueCounts.Keys.Max()!;
            if (value.CompareTo(largest) < 0)
            {
                long delta = RetainedPayloadBudget.Size(value) - RetainedPayloadBudget.Size(largest);
                if (delta > 0) _payload?.Reserve(delta); else _payload?.Release(-delta);
                OtherValueCount += ValueCounts[largest];
                ValueCounts.Remove(largest);
                ValueCounts.Add(value, count);
                return;
            }
        }
        OtherValueCount += count;
    }

    public FieldStatistic ToResult() => new()
    {
        FieldName = FieldName, Count = Count, NullCount = NullCount,
        TypeCounts = new(TypeCounts, StringComparer.Ordinal), ValueCounts = new(ValueCounts),
        OtherValueCount = OtherValueCount, ValueCountsTruncated = ValueCountsTruncated,
        NumbersAreIntegral = NumbersAreIntegral, NumbersFitInt64 = NumbersFitInt64,
        NumbersFitDecimal = NumbersFitDecimal, NumbersFitDouble = NumbersFitDouble
    };

    internal FieldCountersSnapshot Capture() => new()
    {
        FieldName = FieldName,
        Count = Count,
        NullCount = NullCount,
        TypeCounts = new(TypeCounts),
        ValueCounts = new(ValueCounts),
        OtherValueCount = OtherValueCount,
        ValueCountsTruncated = ValueCountsTruncated,
        NumbersAreIntegral = NumbersAreIntegral,
        NumbersFitInt64 = NumbersFitInt64,
        NumbersFitDecimal = NumbersFitDecimal,
        NumbersFitDouble = NumbersFitDouble,
    };

    internal static FieldAccumulator Restore(FieldCountersSnapshot data, AnalysisResourceLimits? limits = null, RetainedPayloadBudget? payload = null)
    {
        var state = new FieldAccumulator(limits, payload)
        {
            FieldName = data.FieldName,
            Count = data.Count,
            NullCount = data.NullCount,
            TypeCounts = new(data.TypeCounts),
            ValueCounts = new(data.ValueCounts),
            OtherValueCount = data.OtherValueCount,
            ValueCountsTruncated = data.ValueCountsTruncated,
            NumbersAreIntegral = data.NumbersAreIntegral,
            NumbersFitInt64 = data.NumbersFitInt64,
            NumbersFitDecimal = data.NumbersFitDecimal,
            NumbersFitDouble = data.NumbersFitDouble,
        };
        foreach (var value in state.ValueCounts.Keys)
        {
            if (RetainedPayloadBudget.Size(value) > state._limits.MaxFrequencyValueBytes) throw new InvalidDataException("Retained field value exceeds its budget.");
            payload?.Reserve(RetainedPayloadBudget.Size(value));
        }
        return state;
    }
}
