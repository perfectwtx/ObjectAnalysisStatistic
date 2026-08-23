using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ObjectAnalyzer.Analysis;
using ObjectAnalyzer.Parsing;

namespace ObjectAnalyzer.Reporting;

/// <summary>
/// 报告输出前的 PII 打码（redaction）：对敏感字段（手机号/身份证/银行卡/姓名/邮箱）的
/// 值频次键、枚举违规值与重复对象 JSON 进行确定性打码，生成一份"可安全分发的副本"。
/// 无敏感字段时返回原实例（零成本）；重复调用幂等。
/// </summary>
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

        var masksByField = new Dictionary<string, PiiMaskFormat>(StringComparer.Ordinal);
        foreach (var stat in result.FieldStatistics)
        {
            var mask = PiiDetector.TryGetMaskFormat(stat.SemanticType, stat.AppliedRule);
            if (mask != null)
                masksByField[stat.FieldName] = mask;
        }

        if (masksByField.Count == 0)
            return result;

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
            TotalObjects = result.TotalObjects,
            TotalUniqueObjects = result.TotalUniqueObjects,
            TotalDuplicateGroups = result.TotalDuplicateGroups,
            FilterExpression = result.FilterExpression,
            DuplicateObjects = maskedDuplicates,
            FieldStatistics = maskedStats,
            QualityViolations = maskedQuality
        };
    }

    private static FieldStatistic CloneWithMask(FieldStatistic source, PiiMaskFormat mask)
    {
        string MaskKey(string key) => mask.Apply(key);

        var clone = new FieldStatistic
        {
            FieldName = source.FieldName,
            Count = source.Count,
            Coverage = source.Coverage,
            ValueCountsTruncated = source.ValueCountsTruncated,
            MinNumber = source.MinNumber,
            MaxNumber = source.MaxNumber,
            AvgNumber = source.AvgNumber,
            SumNumber = source.SumNumber,
            StdDevNumber = source.StdDevNumber,
            MedianNumber = source.MedianNumber,
            P90Number = source.P90Number,
            P95Number = source.P95Number,
            P99Number = source.P99Number,
            Histogram = source.Histogram,
            MinLength = source.MinLength,
            MaxLength = source.MaxLength,
            AvgLength = source.AvgLength,
            DistinctCount = source.DistinctCount,
            DistinctApproximate = source.DistinctApproximate,
            SemanticType = source.SemanticType,
            NullAppeared = source.NullAppeared,
            TransformFailureCount = source.TransformFailureCount,
            TransformType = source.TransformType,
            AppliedRule = source.AppliedRule
        };

        if (source.DefaultValueRepresentation != null)
        {
            clone.DefaultValueRepresentation = MaskKey(source.DefaultValueRepresentation) ?? source.DefaultValueRepresentation;
        }

        foreach (var kvp in source.TypeCounts)
        {
            clone.TypeCounts[kvp.Key] = kvp.Value;
        }

        foreach (var kvp in source.ValueCounts)
        {
            string masked = MaskKey(kvp.Key) ?? kvp.Key;
            clone.ValueCounts[masked] = clone.ValueCounts.GetValueOrDefault(masked) + kvp.Value;
        }

        foreach (var violation in source.EnumViolations)
        {
            clone.AddEnumViolation(MaskKey(violation) ?? violation);
        }

        return clone;
    }

    private static string MaskDuplicateJson(string json, Dictionary<string, PiiMaskFormat> masksByField)
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