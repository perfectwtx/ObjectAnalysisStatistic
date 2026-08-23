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
        if (!string.IsNullOrEmpty(result.FilterExpression))
        {
            writer.WriteLine($"- 过滤条件: `{result.FilterExpression}`");
        }
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
        writer.WriteLine("| 字段名 | 语义 | 频次/覆盖率 | 主导类型 | 默认值 | 类型分布 | 数值范围 | 长度范围 | 转换 | 枚举违规 | 高频值 |");
        writer.WriteLine("|-------|-----|------------|---------|-------|---------|---------|---------|--------|---------|--------|");
        foreach (var stat in result.FieldStatistics)
        {
            string coverage = stat.Count + "/" + result.TotalObjects + " (" + (stat.Coverage * 100).ToString("F1", CultureInfo.InvariantCulture) + "%)";
            if (stat.DistinctCount.HasValue)
            {
                coverage += stat.DistinctApproximate
                    ? "<br>唯一≈" + stat.DistinctCount.Value.ToString(CultureInfo.InvariantCulture)
                    : "<br>唯一" + stat.DistinctCount.Value.ToString(CultureInfo.InvariantCulture);
            }
            string semantic = !string.IsNullOrEmpty(stat.SemanticType) ? EscapeMd(stat.SemanticType) : "—";
            string defaultVal = stat.DefaultValueRepresentation ?? "—";
            if (stat.NullAppeared && stat.DefaultValueRepresentation == null)
                defaultVal = "null";
            string types = string.Join("<br>", stat.TypeCounts.Select(t => $"{EscapeMd(t.Key)}({t.Value})"));
            if (string.IsNullOrEmpty(types)) types = "—";
            string numeric = "—";
            if (stat.MinNumber.HasValue && stat.MaxNumber.HasValue)
            {
                numeric = $"{stat.MinNumber:G4} ~ {stat.MaxNumber:G4}<br>均值 {stat.AvgNumber:G4}";
                if (stat.StdDevNumber.HasValue)
                {
                    numeric += $" σ {stat.StdDevNumber:G3}";
                }

                if (stat.FormatQuantiles() is { } quantiles)
                {
                    numeric += $"<br>{EscapeMd(quantiles)}";
                }

                if (stat.Histogram is { Count: > 0 })
                {
                    numeric += "<br>" + EscapeMd(string.Join(" ", stat.Histogram.Select(b => $"{b}×{b.Count}")));
                }
            }
            string length = "—";
            if (stat.MinLength.HasValue && stat.MaxLength.HasValue)
            {
                length = $"{stat.MinLength} ~ {stat.MaxLength} 字符";
                if (stat.AvgLength.HasValue)
                {
                    length += $"<br>平均 {stat.AvgLength:G3}";
                }
            }
            string transform = stat.TransformType != null
                ? (stat.TransformFailureCount > 0
                    ? $"{EscapeMd(stat.TransformType)} (失败 {stat.TransformFailureCount})"
                    : EscapeMd(stat.TransformType))
                : "—";
            string violations = stat.EnumViolations.Count > 0
                ? string.Join(", ", stat.EnumViolations.Select(v => $"`{EscapeMd(v)}`"))
                : "—";
            foreach (var check in result.QualityViolations)
            {
                if (check.Check == QualityCheckTypes.EnumValues ||
                    !string.Equals(check.Field, stat.FieldName, StringComparison.Ordinal))
                    continue;
                string approxMark = check.Approximate ? "(近似)" : "";
                violations += $"<br>{EscapeMd(QualityCheckTypes.Label(check.Check))}{approxMark}: {EscapeMd(check.Message)}";
                if (check.Samples.Count > 0)
                {
                    violations += "<br>" + string.Join(", ", check.Samples.Select(s => $"`{EscapeMd(Truncate(s, 40))}`"));
                }
            }
            string topValues = string.Join("<br>", stat.ValueCounts
                .OrderByDescending(v => v.Value).Take(5)
                .Select(v => $"`{EscapeMd(Truncate(v.Key, 40))}` ×{v.Value}"));

            writer.Write("| `");
            writer.Write(EscapeMd(stat.FieldName));
            writer.Write("` | ");
            writer.Write(semantic);
            writer.Write(" | ");
            writer.Write(coverage);
            writer.Write(" | ");
            writer.Write(EscapeMd(stat.PrimaryType));
            writer.Write(" | ");
            writer.Write(EscapeMd(defaultVal));
            writer.Write(" | ");
            writer.Write(types);
            writer.Write(" | ");
            writer.Write(numeric);
            writer.Write(" | ");
            writer.Write(length);
            writer.Write(" | ");
            writer.Write(transform);
            writer.Write(" | ");
            writer.Write(violations);
            writer.Write(" | ");
            writer.Write(topValues);
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

    private static string Truncate(string s, int maxLen)
    {
        return s.Length <= maxLen ? s : s[..(maxLen - 3)] + "...";
    }
}
