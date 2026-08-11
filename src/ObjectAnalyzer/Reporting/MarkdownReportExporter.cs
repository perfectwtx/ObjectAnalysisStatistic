using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Spectre.Console;

namespace ObjectAnalyzer.Reporting;

/// <summary>
/// 将分析结果导出为 Markdown 报告。
/// </summary>
public static class MarkdownReportExporter
{
    public static void Save(AnalysisResult result, string outputPath)
    {
        AnsiConsole.MarkupLine($"正在将 Markdown 报告保存至: [blue]{Markup.Escape(outputPath)}[/] ...");
        // 逐段写入，避免大报告整体常驻内存
        using (var writer = new StreamWriter(outputPath, false, Encoding.UTF8))
        {
            WriteMarkdown(writer, result);
        }
        AnsiConsole.MarkupLine($"[green]Markdown 报告成功保存！[/]");
    }

    private static void WriteMarkdown(TextWriter writer, AnalysisResult result)
    {
        writer.WriteLine("# ObjectAnalyzer 分析报告");
        writer.WriteLine();
        writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"- 总对象数: {result.TotalObjects}"));
        writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"- 唯一对象数: {result.TotalUniqueObjects}"));
        writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"- 重复组数: {result.TotalDuplicateGroups}"));
        writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"- 字段数: {result.FieldStatistics.Count}"));
        writer.WriteLine();

        // 数据质量摘要
        var quality = DataQualityReport.Build(result);
        writer.WriteLine("## 数据质量");
        writer.WriteLine();
        if (quality.Passed)
        {
            writer.WriteLine($"✅ **通过** — {quality.TotalFields} 个字段，无枚举违规。");
        }
        else
        {
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"❌ **未通过** — {quality.FieldsWithViolations}/{quality.TotalFields} 个字段存在 {quality.TotalViolationCount} 条枚举违规（违规率 {quality.ViolationRate:P1}）。"));
        }
        writer.WriteLine();

        if (result.TotalDuplicateGroups > 0)
        {
            writer.WriteLine("## 重复对象");
            writer.WriteLine();
            writer.WriteLine("| 重复次数 | 对象内容 |");
            writer.WriteLine("|---------|---------|");
            foreach (var dup in result.DuplicateObjects)
            {
                writer.Write("| ");
                writer.Write(dup.Count.ToString(CultureInfo.InvariantCulture));
                writer.Write(" | `");
                writer.Write(EscapeMd(dup.ObjectJson));
                writer.WriteLine(" |");
            }
            if (result.TotalDuplicateGroups > result.DuplicateObjects.Count)
            {
                writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"> 完整 JSON 已省略，仅展示前 {result.DuplicateObjects.Count} 组示例（共 {result.TotalDuplicateGroups} 组）。"));
            }
            writer.WriteLine();
        }

        writer.WriteLine("## 字段详情");
        writer.WriteLine();
        writer.WriteLine("| 字段名 | 频次 | 覆盖率 | 主导类型 | 默认值 | 唯一值 | 枚举违规 |");
        writer.WriteLine("|-------|------|--------|---------|-------|-------|---------|");
        foreach (var stat in result.FieldStatistics)
        {
            string coverage = (stat.Coverage * 100).ToString("F1", CultureInfo.InvariantCulture) + "%";
            string defaultVal = stat.DefaultValueRepresentation ?? "—";
            string unique = stat.ValueCountsTruncated
                ? stat.ValueCounts.Count.ToString(CultureInfo.InvariantCulture) + "+"
                : stat.ValueCounts.Count.ToString(CultureInfo.InvariantCulture);
            string violations = stat.EnumViolations.Count > 0
                ? string.Join(", ", stat.EnumViolations.Select(v => $"`{EscapeMd(v)}`"))
                : "";

            writer.Write("| `");
            writer.Write(EscapeMd(stat.FieldName));
            writer.Write("` | ");
            writer.Write(stat.Count.ToString(CultureInfo.InvariantCulture));
            writer.Write(" | ");
            writer.Write(coverage);
            writer.Write(" | ");
            writer.Write(EscapeMd(stat.PrimaryType));
            writer.Write(" | ");
            writer.Write(EscapeMd(defaultVal));
            writer.Write(" | ");
            writer.Write(unique);
            writer.Write(" | ");
            writer.Write(violations);
            writer.WriteLine(" |");
        }
        writer.WriteLine();
    }

    private static string EscapeMd(string s)
    {
        // 先转义 < > 防止行内 HTML 注入，再插入换行 <br>
        return s.Replace("<", "&lt;").Replace(">", "&gt;")
            .Replace("|", "\\|").Replace("\r", " ").Replace("\n", "<br>");
    }
}
