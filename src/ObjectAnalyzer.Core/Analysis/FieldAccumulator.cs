namespace ObjectAnalyzer.Analysis;

/// <summary>Observed field counters. Derived report values never participate in merging.</summary>
internal sealed class FieldAccumulator
{
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
        if (ValueCounts.TryGetValue(value, out long current)) { ValueCounts[value] = current + count; return; }
        if (ValueCounts.Count < limit) { ValueCounts.Add(value, count); return; }
        ValueCountsTruncated = true;
        // Keep the smallest identities. The threshold only decreases, allowing exact counts
        // for retained values across arbitrary partitions without keeping all identities.
        if (limit > 0)
        {
            RecordValue largest = ValueCounts.Keys.Max()!;
            if (value.CompareTo(largest) < 0)
            {
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

    internal static FieldAccumulator Restore(FieldCountersSnapshot data)
    {
        var state = new FieldAccumulator
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
        return state;
    }
}
