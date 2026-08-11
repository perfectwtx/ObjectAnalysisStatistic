using System;
using System.Collections.Generic;
using System.Linq;

namespace ObjectAnalyzer;

/// <summary>数据质量报告中单个字段的质量明细。</summary>
public sealed class DataQualityFieldRow
{
    public string Field { get; init; } = "";
    public long Count { get; init; }
    public double Coverage { get; init; }
    public string PrimaryType { get; init; } = "";
    public bool HasRule { get; init; }
    public bool HasDefaultValue { get; init; }
    public int ViolationCount { get; init; }
    public IReadOnlyList<string> Violations { get; init; } = Array.Empty<string>();
}

/// <summary>
/// 数据质量报告：将字段画像与规则校验（枚举违规）合并为一份带
/// Pass/Fail 摘要与违规率的结论，供 CI 与人工快速判断数据质量。
/// </summary>
public sealed class DataQualityReport
{
    public bool Passed { get; init; }
    public int TotalFields { get; init; }
    public int FieldsWithViolations { get; init; }
    public int TotalViolationCount { get; init; }

    /// <summary>存在违规的字段占比（0.0 ~ 1.0）。</summary>
    public double ViolationRate { get; init; }

    public List<DataQualityFieldRow> Fields { get; init; } = new();

    public static DataQualityReport Build(AnalysisResult result)
    {
        var rows = result.FieldStatistics
            .OrderByDescending(f => f.EnumViolations.Count)
            .ThenBy(f => f.FieldName, StringComparer.OrdinalIgnoreCase)
            .Select(f => new DataQualityFieldRow
            {
                Field = f.FieldName,
                Count = f.Count,
                Coverage = f.Coverage,
                PrimaryType = f.PrimaryType,
                HasRule = f.AppliedRule != null,
                HasDefaultValue = f.DefaultValueRepresentation != null,
                ViolationCount = f.EnumViolations.Count,
                Violations = f.EnumViolations
            })
            .ToList();

        int totalFields = rows.Count;
        int fieldsWithViolations = rows.Count(r => r.ViolationCount > 0);
        int totalViolationCount = rows.Sum(r => r.ViolationCount);

        return new DataQualityReport
        {
            Passed = totalViolationCount == 0,
            TotalFields = totalFields,
            FieldsWithViolations = fieldsWithViolations,
            TotalViolationCount = totalViolationCount,
            ViolationRate = totalFields > 0 ? (double)fieldsWithViolations / totalFields : 0,
            Fields = rows
        };
    }
}
