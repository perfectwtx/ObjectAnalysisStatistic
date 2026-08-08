using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Spectre.Console;

namespace ObjectAnalyzer;

/// <summary>
/// 将分析结果导出为 Markdown 报告。
/// </summary>
public static class MarkdownReportExporter
{
    public static void Save(AnalysisResult result, string outputPath)
    {
        AnsiConsole.MarkupLine($"正在将 Markdown 报告保存至: [blue]{Markup.Escape(outputPath)}[/] ...");
        File.WriteAllText(outputPath, GenerateMarkdown(result), Encoding.UTF8);
        AnsiConsole.MarkupLine($"[green]Markdown 报告成功保存！[/]");
    }

    private static string GenerateMarkdown(AnalysisResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# ObjectAnalyzer 分析报告");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"- 总对象数: {result.TotalObjects}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- 唯一对象数: {result.TotalUniqueObjects}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- 重复组数: {result.DuplicateObjects.Count}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- 字段数: {result.FieldStatistics.Count}");
        sb.AppendLine();

        if (result.DuplicateObjects.Count > 0)
        {
            sb.AppendLine("## 重复对象");
            sb.AppendLine();
            sb.AppendLine("| 重复次数 | 对象内容 |");
            sb.AppendLine("|---------|---------|");
            foreach (var dup in result.DuplicateObjects)
            {
                sb.Append("| ").Append(dup.Count.ToString(CultureInfo.InvariantCulture))
                    .Append(" | `").Append(EscapeMd(dup.ObjectJson)).AppendLine(" |");
            }
            sb.AppendLine();
        }

        sb.AppendLine("## 字段详情");
        sb.AppendLine();
        sb.AppendLine("| 字段名 | 频次 | 覆盖率 | 主导类型 | 默认值 | 唯一值 | 枚举违规 |");
        sb.AppendLine("|-------|------|--------|---------|-------|-------|---------|");
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

            sb.Append("| `").Append(EscapeMd(stat.FieldName)).Append("` | ")
                .Append(stat.Count.ToString(CultureInfo.InvariantCulture)).Append(" | ")
                .Append(coverage).Append(" | ")
                .Append(EscapeMd(stat.PrimaryType)).Append(" | ")
                .Append(EscapeMd(defaultVal)).Append(" | ")
                .Append(unique).Append(" | ")
                .Append(violations).AppendLine(" |");
        }
        sb.AppendLine();

        return sb.ToString();
    }

    private static string EscapeMd(string s)
    {
        // 先转义 < > 防止行内 HTML 注入，再插入换行 <br>
        return s.Replace("<", "&lt;").Replace(">", "&gt;")
            .Replace("|", "\\|").Replace("\r", " ").Replace("\n", "<br>");
    }
}
