namespace ObjectAnalyzer.Analysis;

using System.Collections.Generic;
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

    /// <summary>从嵌套对象和对象集合中发现的数据关系。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ObjectRelationship>? ObjectRelationships { get; set; }

    // ── P6.2 增量分析 / 断点续跑：AnalysisResult 级别合并 ──

    /// <summary>
    /// 把另一份 <see cref="AnalysisResult"/> 合并进本对象。
    ///
    /// 合并策略：
    /// - <see cref="TotalObjects"/> / <see cref="TotalDuplicateGroups"/>：直接相加
    /// - <see cref="TotalUniqueObjects"/>：取 max（HLL 未持久化时是无信息损失上界）
    /// - <see cref="DuplicateObjects"/>：追加（去重 key 集合按对象哈希）
    /// - <see cref="QualityViolations"/>：追加
    /// - <see cref="FieldStatistics"/>：按 FieldName 查找已有条目并 <see cref="FieldStatistic.MergeFrom"/>
    /// - 深度分析 维度（CorrelationMatrix / StringPatterns / UnicodeHygiene / FuzzyDuplicates /
    ///   TimeSeriesAnalyses / DistributionDiff / SamplingConfidence / SchemaDiff）：
    ///   仅在 <c>this</c> 为空时采用 <c>other</c>，避免覆盖当前 run 的结果（深度分析 不可增量合并）
    /// - ObjectRelationships：按 Kind + Path 合并计数并合并目标字段摘要
    /// - <see cref="FilterExpression"/>：取 this（确定性优先）
    ///
    /// 不会抛异常——空对象合并保持幂等；用于跨多次增量 run 累积。
    /// </summary>
    public void MergeFrom(AnalysisResult other)
    {
        if (other is null) return;

        TotalObjects += other.TotalObjects;
        TotalDuplicateGroups += other.TotalDuplicateGroups;
        TotalUniqueObjects = Math.Max(TotalUniqueObjects, other.TotalUniqueObjects);

        // FieldStatistics 字典合并：按 FieldName 查找
        if (other.FieldStatistics is { Count: > 0 })
        {
            var byName = new Dictionary<string, FieldStatistic>(FieldStatistics.Count);
            foreach (var f in FieldStatistics) byName[f.FieldName] = f;
            foreach (var incoming in other.FieldStatistics)
            {
                if (byName.TryGetValue(incoming.FieldName, out var existing))
                {
                    existing.MergeFrom(incoming);
                }
                else
                {
                    FieldStatistics.Add(incoming);
                }
            }
        }

        // 重复对象（追加 + 限长保留）
        if (other.DuplicateObjects is { Count: > 0 })
        {
            DuplicateObjects.AddRange(other.DuplicateObjects);
        }

        // 质量违规（追加）
        if (other.QualityViolations is { Count: > 0 })
        {
            QualityViolations.AddRange(other.QualityViolations);
        }

        if (other.ObjectRelationships is { Count: > 0 })
        {
            ObjectRelationships ??= new List<ObjectRelationship>();
            var byKey = ObjectRelationships.ToDictionary(
                x => $"{x.Kind}\n{x.Path}",
                StringComparer.OrdinalIgnoreCase);
            foreach (var incoming in other.ObjectRelationships)
            {
                string key = $"{incoming.Kind}\n{incoming.Path}";
                if (!byKey.TryGetValue(key, out var existing))
                {
                    existing = new ObjectRelationship
                    {
                        Path = incoming.Path,
                        Kind = incoming.Kind
                    };
                    ObjectRelationships.Add(existing);
                    byKey[key] = existing;
                }

                existing.OccurrenceCount += incoming.OccurrenceCount;
                existing.NonNullCount += incoming.NonNullCount;
                foreach (var field in incoming.TargetFields)
                {
                    if (!existing.TargetFields.Contains(field, StringComparer.OrdinalIgnoreCase)
                        && existing.TargetFields.Count < 50)
                    {
                        existing.TargetFields.Add(field);
                    }
                }
                existing.TargetFieldCount = Math.Max(existing.TargetFieldCount, existing.TargetFields.Count);
            }
        }

        // 深度分析 维度：this 为空时采用 other（避免覆盖当前 run）
        CorrelationMatrix ??= other.CorrelationMatrix;
        FuzzyDuplicates ??= other.FuzzyDuplicates;
        StringPatterns ??= other.StringPatterns is not null
            ? new Dictionary<string, StringPatternProfile>(other.StringPatterns)
            : null;
        UnicodeHygiene ??= other.UnicodeHygiene is not null
            ? new Dictionary<string, UnicodeHygieneReport>(other.UnicodeHygiene)
            : null;
        TimeSeriesAnalyses ??= other.TimeSeriesAnalyses is not null
            ? new Dictionary<string, TimeSeriesAnalysis>(other.TimeSeriesAnalyses)
            : null;
        DistributionDiff ??= other.DistributionDiff;
        SamplingConfidence ??= other.SamplingConfidence;
        SchemaDiff ??= other.SchemaDiff;

        // FilterExpression：取 this（确定性优先；other 的过滤条件只用于补充本 run 缺省值）
        FilterExpression ??= other.FilterExpression;
    }

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