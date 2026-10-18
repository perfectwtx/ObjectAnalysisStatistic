namespace ObjectAnalyzer.Analysis;

using System.Collections.Generic;
using System.Text.Json.Serialization;

/// <summary>Report projection. It carries no resumable calculation state or merge behavior.</summary>
public class AnalysisResult
{
    public List<FieldPrivacyDirective>? PrivacyDirectives { get; set; }
    public ResultProjectionInfo? Projection { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AnalysisReliability? Reliability { get; set; }

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

    /// <summary>从嵌套对象和对象集合中发现的数据关系。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ObjectRelationship>? ObjectRelationships { get; set; }

    /// <summary>
    /// Schema 演进的 diff 结果（--diff-schema 指定基线时计算）。未启用时为 null。
    /// 由 ReportExporter / ConsoleReporter 用于追加「Schema 演进」小节。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SchemaDiff? SchemaDiff { get; set; }

    // ── 深度分析 分析深度增强 ──

    /// <summary>
    /// 字段间相关性矩阵（Pearson / Spearman），基于流式 Reservoir 采样。
    /// 字段数 ≥ 2 且各字段至少 3 个非空数值样本时才会填充。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FieldCorrelationMatrix? CorrelationMatrix { get; set; }

    /// <summary>
    /// 模糊去重结果（基于规范化键 + 词袋 Jaccard）。
    /// 仅当 <c>--fuzzy-dedup-field</c> 指定去重目标字段时计算；否则为 null。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FuzzyDuplicateReport? FuzzyDuplicates { get; set; }

    /// <summary>
    /// 字段 → 字符串模式归纳（自动发现 "###-####" / "[a-z]+@[a-z]+" 等形态模板及覆盖率）。
    /// 仅在 --string-patterns 启用时填充；否则为 null。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, StringPatternProfile>? StringPatterns { get; set; }

    /// <summary>
    /// 字段 → Unicode 卫生检查报告（全/半角混用、不可见字符、控制字符、大小写混用等）。
    /// 仅在 --unicode-hygiene 启用时填充；否则为 null。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, UnicodeHygieneReport>? UnicodeHygiene { get; set; }

    /// <summary>
    /// 字段 → 时间序列深度分析（趋势、周期、间隙、脉冲性）。
    /// 仅在 --time-series 启用时填充；否则为 null。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, TimeSeriesAnalysis>? TimeSeriesAnalyses { get; set; }

    /// <summary>
    /// 值分布漂移 diff（--diff-distribution 指定基线快照时计算）。
    /// 与 SchemaDiff 互补：SchemaDiff 看结构变化，本类看分布变化（均值/分位/null 率/distinct）。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DistributionDiff? DistributionDiff { get; set; }

    /// <summary>
    /// 采样不确定性（置信区间）。仅当启用 sampling（--sample-reservoir / --sample-step）时填充。
    /// 对覆盖率 / 数值统计的 95% 置信区间做评估，提示低覆盖字段的统计可信度。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SamplingConfidence? SamplingConfidence { get; set; }
}
