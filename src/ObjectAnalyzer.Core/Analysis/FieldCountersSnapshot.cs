namespace ObjectAnalyzer.Analysis;

/// <summary>Versioned persistence data; no calculation behavior.</summary>
public sealed class FieldCountersSnapshot
{
    public string FieldName { get; set; } = "";
    public long Count { get; set; }
    public long NullCount { get; set; }
    public Dictionary<string, long> TypeCounts { get; set; } = new();
    public Dictionary<RecordValue, long> ValueCounts { get; set; } = new();
    public long OtherValueCount { get; set; }
    public bool ValueCountsTruncated { get; set; }
    public bool NumbersAreIntegral { get; set; }
    public bool NumbersFitInt64 { get; set; }
    public bool NumbersFitDecimal { get; set; }
    public bool NumbersFitDouble { get; set; }
}
