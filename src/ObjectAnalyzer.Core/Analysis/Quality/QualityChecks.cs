using System.Globalization;
using System.Text.RegularExpressions;

namespace ObjectAnalyzer.Analysis;

/// <summary>
/// 质量检查 v2 统一评估器：pattern / min-max / required / unique / nullRateMax / 数据集期望。
/// 在分析收尾（规则已应用、指标已聚合）后单遍评估，输出结构化违规清单；
/// 结果进入 AnalysisResult.QualityViolations，由 --violations 导出与 --fail-on-violations 门禁消费。
/// </summary>
public static class QualityChecks
{
    /// <summary>每条违规携带的最大样本数。</summary>
    public const int MaxSamples = 5;


    /// <summary>Validation/compiler helper. The owning rule plan controls regex lifetime.</summary>
    public static Regex CompilePattern(string pattern) =>
        new(pattern, RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(250));

    /// <summary>对分析结果执行全部质量检查（含枚举约束的聚合条目与数据集期望）。</summary>
    public static List<FieldViolation> Evaluate(
        long totalObjects,
        long uniqueObjects,
        IEnumerable<FieldStatistic> stats,
        AnalysisRules rules,
        RuleExecutionPlan? plan = null)
    {
        var violations = new List<FieldViolation>();

        var fields = stats.ToArray();
        foreach (var stat in fields)
        {
            foreach (var check in stat.ValueRuleResults ?? new())
            {
                if (check.Value.Count == 0) continue;
                violations.Add(new FieldViolation
                {
                    Field = stat.FieldName, Check = check.Key, Count = check.Value.Count,
                    DistinctValueCount = check.Value.DistinctValueCount, DistinctApproximate = check.Value.DistinctApproximate,
                    Message = $"{check.Value.Count} 个违规值：{check.Value.Message}", Samples = check.Value.Samples
                });
            }

        }
        plan ??= new RuleExecutionPlan(rules, FieldAnalyzerRegistry.Default);
        violations.AddRange(plan.EvaluateResults(new(totalObjects, uniqueObjects, fields)));

        return violations;
    }

    internal static void EvaluateNulls(
        FieldStatistic stat, FieldRule rule, long totalObjects, List<FieldViolation> violations)
    {
        long nullCount = stat.TypeCounts.GetValueOrDefault("Null");
        long missingCount = Math.Max(0, totalObjects - stat.Count);
        long badRows = nullCount + missingCount;
        double rate = totalObjects > 0 ? (double)badRows / totalObjects : 0;

        if (rule.Required == true && badRows > 0)
        {
            violations.Add(new FieldViolation
            {
                Field = stat.FieldName,
                Check = QualityCheckTypes.Required,
                Count = badRows,
                Message = badRows switch
                {
                    _ when missingCount == 0 => $"必填字段存在 {badRows} 个 null 值",
                    _ when nullCount == 0 => $"必填字段在 {missingCount} 个对象中缺失",
                    _ => $"必填字段缺失 {missingCount} 行 + null {nullCount} 行"
                }
            });
        }

        if (rule.NullRateMax.HasValue && rate > rule.NullRateMax.Value)
        {
            violations.Add(new FieldViolation
            {
                Field = stat.FieldName,
                Check = QualityCheckTypes.NullRate,
                Count = badRows,
                Message = string.Create(CultureInfo.InvariantCulture,
                    $"null 率 {rate:P1} 超过上限 {rule.NullRateMax.Value:P1}（null {nullCount} + 缺失 {missingCount} 行 / 共 {totalObjects} 行）")
            });
        }
    }

    internal static void EvaluateUnique(FieldStatistic stat, List<FieldViolation> violations)
    {
        long nullCount = stat.TypeCounts.GetValueOrDefault("Null");
        long nonNull = stat.Count - nullCount;
        if (nonNull <= 0)
            return;

        long duplicates;
        bool approximate = false;
        var samples = new List<string>();

        if (!stat.ValueCountsTruncated && stat.DistinctCount.HasValue && !stat.DistinctApproximate)
        {
            // 精确：distinct 含 null 键时扣除
            long distinctNonNull = stat.DistinctCount!.Value - (nullCount > 0 ? 1 : 0);
            duplicates = Math.Max(0, nonNull - distinctNonNull);

            if (duplicates > 0)
            {
                foreach (var kv in stat.ValueCounts.OrderByDescending(v => v.Value))
                {
                    if (samples.Count >= MaxSamples)
                        break;
                    if (kv.Key.Kind == RecordValueKind.Null || kv.Value <= 1)
                        continue;
                    samples.Add($"{kv.Key}×{kv.Value}");
                }
            }
        }
        else if (stat.DistinctCount.HasValue)
        {
            // 截断：HyperLogLog 近似（HLL 与精确频次使用相同值域，扣除 null 后与非空数比较）
            duplicates = Math.Max(0, nonNull - (stat.DistinctCount!.Value - (nullCount > 0 ? 1 : 0)));
            approximate = true;
        }
        else
        {
            return;
        }

        if (duplicates <= 0)
            return;

        violations.Add(new FieldViolation
        {
            Field = stat.FieldName,
            Check = QualityCheckTypes.Unique,
            Count = duplicates,
            Approximate = approximate,
            Message = approximate
                ? $"约 {duplicates} 个重复非空值（唯一值已截断，HyperLogLog 近似）"
                : $"{duplicates} 个重复非空值",
            Samples = samples
        });
    }

    internal static void EvaluateOutlier(FieldStatistic stat, double maxRatio, List<FieldViolation> violations)
    {
        if (!stat.OutlierCount.HasValue || !stat.OutlierRatio.HasValue)
            return;
        if (stat.OutlierRatio.Value <= maxRatio)
            return;

        violations.Add(new FieldViolation
        {
            Field = stat.FieldName,
            Check = QualityCheckTypes.OutlierRatio,
            Count = stat.OutlierCount.Value,
            Approximate = true, // 基于对数直方图近似归类
            Message = string.Create(CultureInfo.InvariantCulture,
                $"异常值比例 {stat.OutlierRatio.Value:P1} 超过上限 {maxRatio:P1}（{stat.OutlierCount.Value} 个样本 / 区间 {stat.OutlierBounds}，{stat.OutlierMethod} 近似）")
        });
    }

    internal static void EvaluateExpectations(
        long totalObjects, long uniqueObjects, AnalysisRules rules, List<FieldViolation> violations)
    {
        var expectations = rules.Expectations;
        if (expectations == null)
            return;

        if (expectations.MinRowCount.HasValue && totalObjects < expectations.MinRowCount.Value)
        {
            violations.Add(new FieldViolation
            {
                Field = "",
                Check = QualityCheckTypes.RowCount,
                Count = totalObjects,
                Message = string.Create(CultureInfo.InvariantCulture,
                    $"行数 {totalObjects} 低于期望下限 {expectations.MinRowCount.Value}")
            });
        }

        if (expectations.MaxDuplicateRate.HasValue && totalObjects > 0)
        {
            double duplicateRate = (double)(totalObjects - uniqueObjects) / totalObjects;
            if (duplicateRate > expectations.MaxDuplicateRate.Value)
            {
                violations.Add(new FieldViolation
                {
                    Field = "",
                    Check = QualityCheckTypes.DuplicateRate,
                    Count = totalObjects - uniqueObjects,
                    Message = string.Create(CultureInfo.InvariantCulture,
                        $"重复率 {duplicateRate:P1} 超过上限 {expectations.MaxDuplicateRate.Value:P1}（{(totalObjects - uniqueObjects)} / {totalObjects} 行完全相同）")
                });
            }
        }
    }

}
