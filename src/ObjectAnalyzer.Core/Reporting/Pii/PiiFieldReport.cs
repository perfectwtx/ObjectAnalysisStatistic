namespace ObjectAnalyzer.Reporting;

/// <summary>合规报告中单个敏感字段的明细。</summary>
public sealed class PiiFieldReport
{
    public string Field { get; init; } = "";
    public string Category { get; init; } = "";
    public string CategoryLabel { get; init; } = "";
    public bool HighSeverity { get; init; }
    public long Count { get; init; }
    public double Coverage { get; init; }
    public int DistinctValues { get; init; }
    public string? MaskedSample { get; init; }
}
