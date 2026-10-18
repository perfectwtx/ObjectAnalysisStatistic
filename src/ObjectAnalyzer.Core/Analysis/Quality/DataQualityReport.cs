namespace ObjectAnalyzer.Analysis;

/// <summary>
/// 数据质量报告：将字段画像与规则校验（枚举违规 + 质量检查 v2 + 数据集期望）合并为一份
/// Pass/Fail 摘要与违规率的结论，供 CI 与人工快速判断数据质量。
/// </summary>
public sealed class DataQualityReport
{
    public bool Passed { get; init; }
    public int TotalFields { get; init; }
    public int FieldsWithViolations { get; init; }
    public long TotalViolationCount { get; init; }

    /// <summary>存在违规的字段占比（0.0 ~ 1.0）。</summary>
    public double ViolationRate { get; init; }

    /// <summary>数据集级期望违规（minRowCount / maxDuplicateRate）。</summary>
    public IReadOnlyList<FieldViolation> DatasetViolations { get; init; } = Array.Empty<FieldViolation>();

    public List<DataQualityFieldRow> Fields { get; init; } = new();

    public static DataQualityReport Build(AnalysisResult result)
    {
        // 非枚举的质量检查条目按字段分组；枚举违规作为字段行的逐值 Violations 列表
        var checksByField = result.QualityViolations
            .Where(v => !string.IsNullOrEmpty(v.Field) && v.Check != QualityCheckTypes.EnumValues)
            .GroupBy(v => v.Field, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<FieldViolation>)g.ToList(), StringComparer.Ordinal);

        var rows = result.FieldStatistics
            .Select(f => new DataQualityFieldRow
            {
                Field = f.FieldName,
                Count = f.Count,
                Coverage = f.Coverage,
                PrimaryType = f.PrimaryType,
                HasRule = f.AppliedRule != null,
                HasDefaultValue = f.DefaultValueRepresentation != null,
                ViolationCount = f.EnumViolationCount + (checksByField.TryGetValue(f.FieldName, out var checks)
                    ? checks.Sum(c => c.Count)
                    : 0),
                Violations = f.EnumViolations,
                Checks = checksByField.TryGetValue(f.FieldName, out var fieldChecks)
                    ? fieldChecks
                    : Array.Empty<FieldViolation>()
            })
            .OrderByDescending(r => r.ViolationCount)
            .ThenBy(r => r.Field, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var datasetViolations = result.QualityViolations
            .Where(v => string.IsNullOrEmpty(v.Field))
            .ToList();

        int totalFields = rows.Count;
        int fieldsWithViolations = rows.Count(r => r.ViolationCount > 0);
        long totalViolationCount = rows.Sum(r => r.ViolationCount)
            + datasetViolations.Sum(v => Math.Max(1, v.Count));

        return new DataQualityReport
        {
            Passed = totalViolationCount == 0,
            TotalFields = totalFields,
            FieldsWithViolations = fieldsWithViolations,
            TotalViolationCount = totalViolationCount,
            ViolationRate = totalFields > 0 ? (double)fieldsWithViolations / totalFields : 0,
            DatasetViolations = datasetViolations,
            Fields = rows
        };
    }
}
