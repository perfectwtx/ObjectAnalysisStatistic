using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Spectre.Console;

namespace ObjectAnalyzer.Reporting;

/// <summary>
/// 将分析结果导出为 CSV 报告（每个字段一行，便于表格工具/CI 消费）。
/// </summary>
public static class CsvReportExporter
{
    public static void Save(AnalysisResult result, string outputPath)
    {
        AnsiConsole.MarkupLine($"正在将 CSV 报告保存至: [blue]{Markup.Escape(outputPath)}[/] ...");
        // 带 BOM 的 UTF-8，保证 Excel 直接打开中文不乱码；逐行写入避免整份报告常驻内存
        using (var writer = new StreamWriter(outputPath, false, new UTF8Encoding(true)))
        {
            WriteCsv(writer, result);
        }
        AnsiConsole.MarkupLine($"[green]CSV 报告成功保存！[/]");
    }

    private static void WriteCsv(TextWriter writer, AnalysisResult result)
    {
        // Coverage 输出 0.0~1.0 的机器可读小数，方便 CI/表格直接做数值计算
        writer.WriteLine("FieldName,Count,Coverage,PrimaryType,DefaultValue,UniqueValues,Truncated,TransformType,TransformFailures,EnumViolations");

        foreach (var stat in result.FieldStatistics)
        {
            string coverage = stat.Coverage.ToString("F4", CultureInfo.InvariantCulture);
            string unique = stat.ValueCounts.Count.ToString(CultureInfo.InvariantCulture);
            string truncated = stat.ValueCountsTruncated ? "true" : "false";
            string transformType = stat.TransformType ?? "";
            string transformFailures = stat.TransformFailureCount.ToString(CultureInfo.InvariantCulture);
            string violations = string.Join(";", stat.EnumViolations);

            writer.Write(CsvField(stat.FieldName)); writer.Write(',');
            writer.Write(stat.Count.ToString(CultureInfo.InvariantCulture)); writer.Write(',');
            writer.Write(coverage); writer.Write(',');
            writer.Write(CsvField(stat.PrimaryType)); writer.Write(',');
            writer.Write(CsvField(stat.DefaultValueRepresentation ?? "")); writer.Write(',');
            writer.Write(unique); writer.Write(',');
            writer.Write(truncated); writer.Write(',');
            writer.Write(CsvField(transformType)); writer.Write(',');
            writer.Write(transformFailures); writer.Write(',');
            writer.Write(CsvField(violations)); writer.WriteLine();
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
