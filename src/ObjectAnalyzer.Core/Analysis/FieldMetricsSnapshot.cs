namespace ObjectAnalyzer.Analysis;

/// <summary>Versioned persistence data; no calculation behavior.</summary>
public sealed class FieldMetricsSnapshot
{
    public double MinNumber { get; set; }
    public double MaxNumber { get; set; }
    public double SumNumber { get; set; }
    public long NumberCount { get; set; }
    public double NumMean { get; set; }
    public double NumM2 { get; set; }
    public int MinLength { get; set; }
    public int MaxLength { get; set; }
    public long LengthSum { get; set; }
    public long StringCount { get; set; }
    public long ZeroCount { get; set; }
    public Dictionary<string, long> SemanticCounts { get; set; } = new();
    public Dictionary<int, long>? Histogram { get; set; }
    public long DateTimeCount { get; set; }
    public DateTimeOffset? MinDateTime { get; set; }
    public DateTimeOffset? MaxDateTime { get; set; }
    public Dictionary<long, long>? DateHistogram { get; set; }
    public long RangeViolationCount { get; set; }
    public QuantileStateSnapshot? Quantiles { get; set; }
    public HyperLogLogStateSnapshot? DistinctHll { get; set; }
}
