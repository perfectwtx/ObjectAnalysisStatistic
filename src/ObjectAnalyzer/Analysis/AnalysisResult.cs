namespace ObjectAnalyzer.Analysis;

using System.Text.Json.Serialization;

public class AnalysisResult
{
    public long TotalObjects { get; set; }
    public long TotalUniqueObjects { get; set; }

    /// <summary>重复对象组总数（不受 DuplicateObjects 存储上限影响）。</summary>
    public long TotalDuplicateGroups { get; set; }

    /// <summary>本次分析使用的过滤条件（--where / 规则文件 filter），未启用时省略。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? FilterExpression { get; set; }

    public List<DuplicateObjectStatistic> DuplicateObjects { get; set; } = new();
    public List<FieldStatistic> FieldStatistics { get; set; } = new();
}
