using System.Text.Json;

namespace ObjectAnalyzer.Reporting;

/// <summary>
/// 报告输出前的 PII 打码（redaction）：对敏感字段（手机号/身份证/银行卡/姓名/邮箱）的
/// 值频次键、枚举违规值与重复对象 JSON 进行确定性打码，生成一份"可安全分发的副本"。
/// 无敏感字段时返回原实例（零成本）；重复调用幂等。
/// </summary>
/// <remarks>
/// 打码格式的判定不在本类重新推导，而是消费 <see cref="PiiPolicy.Resolve"/> 一次性解析的策略——
/// 结果报告与数据预览等出站形态共享同一判定。
/// </remarks>
public static class PiiRedactor
{
    /// <summary>
    /// 打码副本。若结果不含任何 PII 字段，则直接返回原引用。
    /// enabled=false（--pii-off）时跳过打码原样返回，供用户显式导出原始值。
    /// </summary>
    public static AnalysisResult Redact(AnalysisResult result, bool enabled = true)
    {
        if (!enabled)
            return result;

        return Redact(result, PiiPolicy.Resolve(result));
    }

    /// <summary>
    /// 按已解析的 <see cref="PiiPolicy"/> 生成打码副本。
    /// 空策略（无敏感字段）直接返回原引用；重复调用幂等。
    /// </summary>
    public static AnalysisResult Redact(AnalysisResult result, PiiPolicy policy)
    {
        if (policy.IsEmpty)
            return result;

        var masksByField = policy.MasksByField;

        var maskedStats = new List<FieldStatistic>(result.FieldStatistics.Count);
        foreach (var stat in result.FieldStatistics)
        {
            if (masksByField.TryGetValue(stat.FieldName, out var mask))
            {
                maskedStats.Add(CloneWithMask(stat, mask));
            }
            else
            {
                maskedStats.Add(stat);
            }
        }

        var maskedDuplicates = result.DuplicateObjects;
        if (result.DuplicateObjects.Count > 0)
        {
            maskedDuplicates = result.DuplicateObjects
                .Select(d => new DuplicateObjectStatistic
                {
                    Count = d.Count,
                    ObjectJson = MaskDuplicateJson(d.ObjectJson, masksByField)
                })
                .ToList();
        }

        // 质量检查 v2：违规样本可能包含原始敏感值，同步打码后传播
        var maskedQuality = result.QualityViolations.Count == 0
            ? result.QualityViolations
            : result.QualityViolations
                .Select(v =>
                {
                    if (string.IsNullOrEmpty(v.Field) || !masksByField.TryGetValue(v.Field, out var violationMask))
                        return v;
                    return v with
                    {
                        Samples = v.Samples.Select(s => violationMask.Apply(s) ?? s).ToArray()
                    };
                })
                .ToList();

        return new AnalysisResult
        {
            Reliability = result.Reliability,
            ObjectRelationships = result.ObjectRelationships,
            TotalObjects = result.TotalObjects,
            TotalUniqueObjects = result.TotalUniqueObjects,
            TotalDuplicateGroups = result.TotalDuplicateGroups,
            FilterExpression = result.FilterExpression,
            DuplicateObjects = maskedDuplicates,
            FieldStatistics = maskedStats,
            QualityViolations = maskedQuality,
            SchemaDiff = result.SchemaDiff,
            // ── 深度分析 字段透传：打码不影响 深度分析 报告内容（相关性矩阵、模糊去重、模式、Unicode 卫生等不含原始值）──
            CorrelationMatrix = result.CorrelationMatrix,
            FuzzyDuplicates = result.FuzzyDuplicates,
            StringPatterns = result.StringPatterns,
            UnicodeHygiene = result.UnicodeHygiene,
            TimeSeriesAnalyses = result.TimeSeriesAnalyses,
            DistributionDiff = result.DistributionDiff,
            SamplingConfidence = result.SamplingConfidence
        };
    }

    private static FieldStatistic CloneWithMask(FieldStatistic source, PiiMaskFormat mask)
    {
        string MaskKey(string key) => mask.Apply(key);

        // 副本底版自带全部标量统计（PrimaryType / 日期剖析 / 异常值 / RangeViolationCount…）
        // 与断开共享的可变集合；这里只重填需要打码的部分。
        var clone = source.CloneForRedaction();
        clone.ValueRuleResults = source.ValueRuleResults?.ToDictionary(p => p.Key,
            p => p.Value with { Samples = p.Value.Samples.Select(s => MaskKey(s) ?? s).ToArray() }, StringComparer.Ordinal);
        // Extension payloads have no redaction schema; omit them for masked fields.
        clone.AnalyzerResults = null;

        if (source.DefaultValueRepresentation != null)
        {
            clone.DefaultValueRepresentation = MaskKey(source.DefaultValueRepresentation) ?? source.DefaultValueRepresentation;
        }

        foreach (var kvp in source.ValueCounts)
        {
            RecordValue masked = kvp.Key.Kind == RecordValueKind.Null
                ? RecordValue.Null
                : RecordValue.FromString(MaskKey(kvp.Key.ToDisplayString()) ?? kvp.Key.ToDisplayString());
            clone.ValueCounts[masked] = clone.ValueCounts.GetValueOrDefault(masked) + kvp.Value;
        }

        foreach (var violation in source.EnumViolations)
        {
            clone.AddEnumViolation(MaskKey(violation) ?? violation);
        }

        return clone;
    }

    private static string MaskDuplicateJson(string json, IReadOnlyDictionary<string, PiiMaskFormat> masksByField)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return json;

            var redacted = new Dictionary<string, object?>(root.EnumerateObject().Count());
            foreach (var property in root.EnumerateObject())
            {
                if (masksByField.TryGetValue(property.Name, out var mask) &&
                    property.Value.ValueKind == JsonValueKind.String)
                {
                    redacted[property.Name] = mask.Apply(property.Value.GetString()!);
                }
                else
                {
                    redacted[property.Name] = property.Value.Clone();
                }
            }
            return JsonSerializer.Serialize(redacted, JsonSharedOptions.Write);
        }
        catch (JsonException)
        {
            return json;
        }
    }
}
