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

    /// <summary>
    /// 质量检查违规清单（规则文件 v2：枚举/范围/模式/必填/唯一/null 率 + 数据集期望）。
    /// 未配置规则时为空列表。
    /// </summary>
    public List<FieldViolation> QualityViolations { get; set; } = new();

    /// <summary>
    /// Schema 演进的 diff 结果（--diff-schema 指定基线时计算）。未启用时为 null。
    /// 由 ReportExporter / ConsoleReporter 用于追加「Schema 演进」小节。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SchemaDiff? SchemaDiff { get; set; }
}
