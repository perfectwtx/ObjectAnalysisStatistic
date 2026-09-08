using System.Globalization;
using System.Text;

namespace ObjectAnalyzer.Reporting;

/// <summary>
/// 将分析结果导出为 CSV 报告（每个字段一行，便于表格工具/CI 消费）。
/// </summary>
public static class CsvReportExporter
{
    public static void Save(AnalysisResult result, string outputPath)
    {
        AnalysisServices.Current.Console.MarkupLine($"正在将 CSV 报告保存至: [blue]{MarkupText.Escape(outputPath)}[/] ...");
        // 带 BOM 的 UTF-8，保证 Excel 直接打开中文不乱码；逐行写入避免整份报告常驻内存
        using (var writer = new StreamWriter(outputPath, false, new UTF8Encoding(true)))
        {
            WriteCsv(writer, result);
        }
        AnalysisServices.Current.Console.MarkupLine($"[green]CSV 报告成功保存！[/]");
    }

    private static void WriteCsv(TextWriter writer, AnalysisResult result)
    {
        // ── 摘要行 ──
        writer.WriteLine("# Summary");
        writer.Write("TotalObjects,"); writer.WriteLine(result.TotalObjects.ToString(CultureInfo.InvariantCulture));
        writer.Write("TotalUniqueObjects,"); writer.WriteLine(result.TotalUniqueObjects.ToString(CultureInfo.InvariantCulture));
        writer.Write("TotalDuplicateGroups,"); writer.WriteLine(result.TotalDuplicateGroups.ToString(CultureInfo.InvariantCulture));
        writer.Write("TotalFields,"); writer.WriteLine(result.FieldStatistics.Count.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrEmpty(result.FilterExpression))
        {
            writer.Write("FilterExpression,"); writer.WriteLine(CsvField(result.FilterExpression));
        }
        writer.WriteLine();

        // ── 数据质量 ──
        var quality = DataQualityReport.Build(result);
        writer.WriteLine("# DataQuality");
        writer.Write("Passed,"); writer.WriteLine(quality.Passed ? "true" : "false");
        writer.Write("FieldsWithViolations,"); writer.WriteLine(quality.FieldsWithViolations.ToString(CultureInfo.InvariantCulture));
        writer.Write("TotalViolationCount,"); writer.WriteLine(quality.TotalViolationCount.ToString(CultureInfo.InvariantCulture));
        writer.Write("ViolationRate,"); writer.WriteLine(quality.ViolationRate.ToString("F4", CultureInfo.InvariantCulture));
        writer.WriteLine();

        // ── 重复对象 ──
        if (result.TotalDuplicateGroups > 0)
        {
            writer.WriteLine("# DuplicateObjects");
            writer.WriteLine("Count,ObjectJson");
            foreach (var dup in result.DuplicateObjects)
            {
                writer.Write(dup.Count.ToString(CultureInfo.InvariantCulture)); writer.Write(',');
                writer.WriteLine(CsvField(dup.ObjectJson));
            }
            writer.WriteLine();
        }

        // ── 字段详情 ──
        // Coverage 输出 0.0~1.0 的机器可读小数，方便 CI/表格直接做数值计算
        writer.WriteLine("# FieldDetails");
        writer.WriteLine("FieldName,Count,Coverage,SemanticType,PrimaryType,DefaultValue,NullAppeared,UniqueValues,DistinctApproximate,Truncated,TypeCounts,MinNumber,MaxNumber,AvgNumber,SumNumber,StdDevNumber,MedianNumber,P90Number,P95Number,P99Number,Histogram,MinLength,MaxLength,AvgLength,TransformType,TransformFailures,EnumViolations,TopValues");

        foreach (var stat in result.FieldStatistics)
        {
            string coverage = stat.Coverage.ToString("F4", CultureInfo.InvariantCulture);
            string semantic = CsvField(stat.SemanticType ?? "");
            string defaultVal = stat.DefaultValueRepresentation ?? "";
            string nullAppeared = stat.NullAppeared ? "true" : "false";
            // UniqueValues：优先真实 distinct 计数（截断时为 HyperLogLog 近似），退化为跟踪的唯一值数
            string unique = (stat.DistinctCount ?? stat.ValueCounts.Count).ToString(CultureInfo.InvariantCulture);
            string distinctApproximate = stat.DistinctApproximate ? "true" : "false";
            string truncated = stat.ValueCountsTruncated ? "true" : "false";
            string typeCounts = CsvField(string.Join(";", stat.TypeCounts.Select(t => $"{t.Key}={t.Value}")));
            string minNum = stat.MinNumber?.ToString("G4", CultureInfo.InvariantCulture) ?? "";
            string maxNum = stat.MaxNumber?.ToString("G4", CultureInfo.InvariantCulture) ?? "";
            string avgNum = stat.AvgNumber?.ToString("G4", CultureInfo.InvariantCulture) ?? "";
            string sumNum = stat.SumNumber?.ToString("G4", CultureInfo.InvariantCulture) ?? "";
            string stdDev = stat.StdDevNumber?.ToString("G4", CultureInfo.InvariantCulture) ?? "";
            string median = stat.MedianNumber?.ToString("G4", CultureInfo.InvariantCulture) ?? "";
            string p90 = stat.P90Number?.ToString("G4", CultureInfo.InvariantCulture) ?? "";
            string p95 = stat.P95Number?.ToString("G4", CultureInfo.InvariantCulture) ?? "";
            string p99 = stat.P99Number?.ToString("G4", CultureInfo.InvariantCulture) ?? "";
            string histogram = stat.Histogram is { Count: > 0 }
                ? CsvField(string.Join(";", stat.Histogram.Select(b => $"{b.ToString()}:{b.Count.ToString(CultureInfo.InvariantCulture)}")))
                : "";
            string minLen = stat.MinLength?.ToString(CultureInfo.InvariantCulture) ?? "";
            string maxLen = stat.MaxLength?.ToString(CultureInfo.InvariantCulture) ?? "";
            string avgLen = stat.AvgLength?.ToString("G4", CultureInfo.InvariantCulture) ?? "";
            string transformType = stat.TransformType ?? "";
            string transformFailures = stat.TransformFailureCount.ToString(CultureInfo.InvariantCulture);
            string violations = CsvField(string.Join(";", stat.EnumViolations));
            string topValues = CsvField(string.Join(";", stat.ValueCounts
                .OrderByDescending(v => v.Value).Take(5)
                .Select(v => $"{v.Key}×{v.Value}")));

            writer.Write(CsvField(stat.FieldName)); writer.Write(',');
            writer.Write(stat.Count.ToString(CultureInfo.InvariantCulture)); writer.Write(',');
            writer.Write(coverage); writer.Write(',');
            writer.Write(semantic); writer.Write(',');
            writer.Write(CsvField(stat.PrimaryType)); writer.Write(',');
            writer.Write(CsvField(defaultVal)); writer.Write(',');
            writer.Write(nullAppeared); writer.Write(',');
            writer.Write(unique); writer.Write(',');
            writer.Write(distinctApproximate); writer.Write(',');
            writer.Write(truncated); writer.Write(',');
            writer.Write(typeCounts); writer.Write(',');
            writer.Write(minNum); writer.Write(',');
            writer.Write(maxNum); writer.Write(',');
            writer.Write(avgNum); writer.Write(',');
            writer.Write(sumNum); writer.Write(',');
            writer.Write(stdDev); writer.Write(',');
            writer.Write(median); writer.Write(',');
            writer.Write(p90); writer.Write(',');
            writer.Write(p95); writer.Write(',');
            writer.Write(p99); writer.Write(',');
            writer.Write(histogram); writer.Write(',');
            writer.Write(minLen); writer.Write(',');
            writer.Write(maxLen); writer.Write(',');
            writer.Write(avgLen); writer.Write(',');
            writer.Write(CsvField(transformType)); writer.Write(',');
            writer.Write(transformFailures); writer.Write(',');
            writer.Write(violations); writer.Write(',');
            writer.Write(topValues); writer.WriteLine();
        }
    }

    private static string CsvField(string value)
    {
        // 防御 CSV 公式注入：以 = + - @ 开头的单元格会被 Excel 当作公式执行
        if (value.Length > 0 && (value[0] == '=' || value[0] == '+' || value[0] == '-' || value[0] == '@'))
        {
            value = "'" + value;
        }

        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
        {
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
        return value;
    }
}
