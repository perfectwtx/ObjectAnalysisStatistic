namespace ObjectAnalyzer;

public class AnalysisResult
{
    public int TotalObjects { get; set; }
    public int TotalUniqueObjects { get; set; }
    public List<DuplicateObjectStatistic> DuplicateObjects { get; set; } = new();
    public List<FieldStatistic> FieldStatistics { get; set; } = new();
}
