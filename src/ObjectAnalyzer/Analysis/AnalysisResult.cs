namespace ObjectAnalyzer.Analysis;

public class AnalysisResult
{
    public long TotalObjects { get; set; }
    public long TotalUniqueObjects { get; set; }

    /// <summary>重复对象组总数（不受 DuplicateObjects 存储上限影响）。</summary>
    public long TotalDuplicateGroups { get; set; }

    public List<DuplicateObjectStatistic> DuplicateObjects { get; set; } = new();
    public List<FieldStatistic> FieldStatistics { get; set; } = new();
}
