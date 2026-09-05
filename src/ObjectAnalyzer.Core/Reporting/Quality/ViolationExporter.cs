using System.Text.Json;

namespace ObjectAnalyzer.Reporting;

/// <summary>
/// 将质量检查违规导出为独立的结构化 JSON 文件，便于 CI 断言/程序消费。
/// 覆盖规则文件 v2 全部维度：枚举 / 数值范围 / 正则模式 / 必填 / 唯一性 / null 率 + 数据集期望。
/// 输出格式：
/// <code>
/// {
///   "summary": { "totalViolations": 3, "fieldsWithViolations": 2 },
///   "fieldViolations": [ { "field", "check", "count", "approximate", "samples", "message" } ],
///   "datasetViolations": [ { "check": "rowCount", ... } ]
/// }
/// </code>
/// 枚举违规导出为单一聚合条目（samples 是前 N 条样本），与其他检查一致。
/// </summary>
public static class ViolationExporter
{
    public static void Save(AnalysisResult result, string outputPath, bool redactPii = true)
    {
        result = PiiRedactor.Redact(result, redactPii);

        var fieldEntries = new List<Dictionary<string, object?>>();
        foreach (var stat in result.FieldStatistics)
        {
            // 枚举违规：聚合为单条 entry（含 samples 前 N 条样本）
            if (stat.EnumViolations.Count > 0)
            {
                fieldEntries.Add(new Dictionary<string, object?>
                {
                    ["field"] = stat.FieldName,
                    ["check"] = QualityCheckTypes.EnumValues,
                    ["count"] = stat.EnumViolations.Count,
                    ["message"] = $"{stat.EnumViolations.Count} 个不在枚举范围内的值",
                    ["samples"] = ((IReadOnlyList<string>)stat.EnumViolations).Take(QualityChecks.MaxSamples).ToArray()
                });
            }

            // 其他质量检查条目按 check 维度展开
            foreach (var check in result.QualityViolations.Where(v =>
                v.Field == stat.FieldName && v.Check != QualityCheckTypes.EnumValues))
            {
                var entry = new Dictionary<string, object?>
                {
                    ["field"] = stat.FieldName,
                    ["check"] = check.Check,
                    ["count"] = check.Count,
                    ["message"] = check.Message
                };
                if (check.Approximate) entry["approximate"] = true;
                if (check.Samples.Count > 0) entry["samples"] = check.Samples;
                fieldEntries.Add(entry);
            }
        }

        var datasetEntries = result.QualityViolations
            .Where(v => string.IsNullOrEmpty(v.Field))
            .Select(v => new Dictionary<string, object?>
            {
                ["check"] = v.Check,
                ["count"] = v.Count,
                ["message"] = v.Message
            })
            .ToList();

        int totalViolations = fieldEntries.Count + datasetEntries.Count;
        var payload = new Dictionary<string, object?>
        {
            ["summary"] = new Dictionary<string, object?>
            {
                ["totalViolations"] = totalViolations,
                ["fieldsWithViolations"] = fieldEntries.Select(e => e["field"]).Distinct().Count()
            },
            ["fieldViolations"] = fieldEntries,
            ["datasetViolations"] = datasetEntries
        };

        string json = JsonSerializer.Serialize(payload, JsonSharedOptions.Write);
        File.WriteAllText(outputPath, json);

        AnsiConsole.MarkupLine(
            $"已导出 [yellow]{totalViolations}[/] 条质量违规（{datasetEntries.Count} 条数据集期望）至: [blue]{Markup.Escape(outputPath)}[/]");
    }
}
