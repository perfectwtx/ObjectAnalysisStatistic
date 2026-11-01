using System.Text.Json;

namespace ObjectAnalyzer.Analysis;

public static partial class ObjectAnalyzer
{
    internal static (AnalysisResult Result, Dictionary<string, MergeableQuantileSketch>? QuantileSketches,
        Dictionary<string, HyperLogLog>? DistinctSketches) Complete(AnalysisAccumulator state)
    {
        var globalStatsMap = new Dictionary<string, FieldAccumulator>(state.Fields, StringComparer.Ordinal);
        var globalMetricsMap = state.Metrics;
        var globalCorrelationCollector = state.CorrelationCollector;
        var globalStringPatterns = state.StringPatterns;
        var globalUnicodeHygiene = state.UnicodeHygiene;
        var features = state.Features;
        var rules = state.Rules;
        var fieldRules = state.FieldRules;
        foreach (string path in state.RulePlan.Paths)
            if (!globalStatsMap.ContainsKey(path))
                globalStatsMap.Add(path, new FieldAccumulator(state.Limits) { FieldName = path, Components = state.CreateComponents(path, global: false) });
        // 后置处理：合并极值、推断默认值与语义分类
        var results = new List<FieldStatistic>();
        foreach (var field in globalStatsMap.Values)
        {
            var stat = field.ToResult();
            stat.Coverage = state.TotalObjects > 0 ? (double)stat.Count / state.TotalObjects : 0;

            stat.NullAppeared = stat.TypeCounts.ContainsKey("Null");
            stat.PrimaryType = stat.TypeCounts.OrderByDescending(t => t.Value).ThenBy(t => t.Key, StringComparer.Ordinal).FirstOrDefault().Key ?? "String";

            if (stat.NullAppeared)
            {
                stat.DefaultValueRepresentation = "null";
            }
            else
            {
                if (stat.PrimaryType == "Number")
                {
                    stat.DefaultValueRepresentation = "0";
                }
                else if (stat.PrimaryType == "Boolean")
                {
                    stat.DefaultValueRepresentation = "false";
                }
                else
                {
                    stat.DefaultValueRepresentation = null;
                }
            }

            field.Components!.Complete(stat);

            // 应用配置规则
            if (fieldRules != null && fieldRules.TryGetValue(stat.FieldName, out var rule))
            {
                stat.AppliedRule = AnalysisAccumulator.Clone(rule);

                if (rule.DefaultValue != null)
                {
                    stat.DefaultValueRepresentation = rule.DefaultValue.ToString();
                }

                if (string.IsNullOrEmpty(rule.PrimaryType) && rule.EnumValues is { Count: > 0 })
                {
                    stat.PrimaryType = InferPrimaryTypeFromEnum(rule.EnumValues);
                }
                else if (!string.IsNullOrEmpty(rule.PrimaryType))
                {
                    stat.PrimaryType = rule.PrimaryType;
                }

            }

            results.Add(stat);
        }

        results.Sort((a, b) =>
        {
            int cmp = b.Count.CompareTo(a.Count);
            if (cmp == 0)
            {
                cmp = string.Compare(a.FieldName, b.FieldName, StringComparison.Ordinal);
            }
            return cmp;
        });

        var duplicates = state.BuildDuplicates();
        long totalUniqueObjects = state.ObjectCounts.Count;
        long totalDuplicateGroups = state.ObjectCounts.Count(kv => kv.Value > 1);

        // 质量检查 v2：枚举/范围/模式/必填/唯一/null 率 + 数据集期望，统一评估
        var qualityViolations = QualityChecks.Evaluate(state.TotalObjects, totalUniqueObjects, results, rules ?? new(), state.RulePlan);

        // 深度分析 后置处理：构建新报告（仅 features 启用时计算，否则全部为 null）
        FieldCorrelationMatrix? correlationMatrix = null;
        Dictionary<string, StringPatternProfile>? stringPatterns = null;
        Dictionary<string, UnicodeHygieneReport>? unicodeHygiene = null;
        Dictionary<string, TimeSeriesAnalysis>? timeSeriesAnalyses = null;

        if (features != null)
        {
            if (features.EnableCorrelation && globalCorrelationCollector != null && globalCorrelationCollector.Seen >= 3)
            {
                // 仅纳入被识别为 Number 的字段，避免对分类变量（如已被语义识别为 DateTime 的字符串）跑相关
                var numericFields = results
                    .Where(s => s.PrimaryType == "Number" && s.Count >= 3)
                    .Select(s => s.FieldName)
                    .ToList();
                if (numericFields.Count >= 2)
                {
                    correlationMatrix = FieldCorrelationCalculator.Compute(globalCorrelationCollector, numericFields);
                }
            }

            if (features.EnableStringPatterns && globalStringPatterns != null)
            {
                stringPatterns = new Dictionary<string, StringPatternProfile>(StringComparer.Ordinal);
                foreach (var (field, profiler) in globalStringPatterns)
                {
                    var profile = profiler.Build();
                    if (profile.TotalSamples > 0)
                    {
                        stringPatterns[field] = profile;
                    }
                }
            }

            if (features.EnableUnicodeHygiene && globalUnicodeHygiene != null)
            {
                unicodeHygiene = new Dictionary<string, UnicodeHygieneReport>(StringComparer.Ordinal);
                foreach (var (field, report) in globalUnicodeHygiene)
                {
                    if (report.TotalSamples > 0)
                    {
                        unicodeHygiene[field] = AnalysisAccumulator.Clone(report);
                    }
                }
            }

            if (features.EnableTimeSeries)
            {
                timeSeriesAnalyses = new Dictionary<string, TimeSeriesAnalysis>(StringComparer.Ordinal);
                foreach (var stat in results)
                {
                    if (stat.DateTimeCount is null || stat.DateHistogram == null) continue;
                    var ts = TimeSeriesAnalyzer.Analyze(stat);
                    if (ts != null && ts.BucketCount >= TimeSeriesAnalyzer.MinBucketsForAnalysis)
                    {
                        timeSeriesAnalyses[stat.FieldName] = ts;
                    }
                }
            }
        }

        // 提取每字段的 t-digest 与 HyperLogLog 草图（仅当确实有数据时），供分布快照 / diff 使用
        Dictionary<string, MergeableQuantileSketch>? quantileSketches = null;
        Dictionary<string, HyperLogLog>? distinctSketches = null;
        foreach (var (field, m) in globalMetricsMap)
        {
            if (m.Quantiles != null && m.Quantiles.TotalWeight > 0)
            {
                (quantileSketches ??= new Dictionary<string, MergeableQuantileSketch>(StringComparer.Ordinal))[field] = MergeableQuantileSketch.Restore(m.Quantiles.Capture());
            }
            if (m.DistinctHll != null && m.DistinctHll.Estimate() > 0)
            {
                (distinctSketches ??= new Dictionary<string, HyperLogLog>(StringComparer.Ordinal))[field] = HyperLogLog.Restore(m.DistinctHll.Capture());
            }
        }

        return (
            new AnalysisResult
            {
                TotalObjects = state.TotalObjects,
                TotalUniqueObjects = totalUniqueObjects,
                TotalDuplicateGroups = totalDuplicateGroups,
                DuplicateObjects = duplicates,
                FieldStatistics = results,
                QualityViolations = qualityViolations,
                CorrelationMatrix = correlationMatrix,
                StringPatterns = stringPatterns,
                UnicodeHygiene = unicodeHygiene,
                TimeSeriesAnalyses = timeSeriesAnalyses
            },
            quantileSketches,
            distinctSketches
        );
    }

    private static string InferPrimaryTypeFromEnum(List<JsonElement> enumElements)
    {
        bool any = false, allNumber = true, allBoolean = true;
        foreach (var elem in enumElements)
        {
            any = true;
            if (elem.ValueKind != JsonValueKind.Number) allNumber = false;
            if (elem.ValueKind != JsonValueKind.True && elem.ValueKind != JsonValueKind.False) allBoolean = false;
        }
        if (any && allNumber) return "Number";
        if (any && allBoolean) return "Boolean";
        return "String";
    }
}
