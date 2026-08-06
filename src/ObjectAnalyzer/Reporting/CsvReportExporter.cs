using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Spectre.Console;

namespace ObjectAnalyzer;

/// <summary>
/// 将分析结果导出为 CSV 报告（每个字段一行，便于表格工具/CI 消费）。
/// </summary>
public static class CsvReportExporter
{
    public static void Save(AnalysisResult result, string outputPath)
    {
        AnsiConsole.MarkupLine($"正在将 CSV 报告保存至: [blue]{Markup.Escape(outputPath)}[/] ...");
        // 带 BOM 的 UTF-8，保证 Excel 直接打开中文不乱码
        File.WriteAllText(outputPath, GenerateCsv(result), new UTF8Encoding(true));
        AnsiConsole.MarkupLine($"[green]CSV 报告成功保存！[/]");
    }

    private static string GenerateCsv(AnalysisResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("FieldName,Count,Coverage,PrimaryType,DefaultValue,UniqueValues,Truncated,EnumViolations");

        foreach (var stat in result.FieldStatistics)
        {
            string coverage = (stat.Coverage * 100).ToString("F2", CultureInfo.InvariantCulture);
            string unique = stat.ValueCounts.Count.ToString(CultureInfo.InvariantCulture);
            string truncated = stat.ValueCountsTruncated ? "true" : "false";
            string violations = string.Join(";", stat.EnumViolations);

            sb.Append(CsvField(stat.FieldName)).Append(',')
                .Append(stat.Count.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(coverage).Append(',')
                .Append(CsvField(stat.PrimaryType)).Append(',')
                .Append(CsvField(stat.DefaultValueRepresentation ?? "")).Append(',')
                .Append(unique).Append(',')
                .Append(truncated).Append(',')
                .Append(CsvField(violations)).AppendLine();
        }

        return sb.ToString();
    }

    private static string CsvField(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
        {
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
        return value;
    }
}
