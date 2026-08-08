using System;
using System.Linq;
using Spectre.Console;

namespace ObjectAnalyzer;

public static class ConsoleReporter
{
    public static void Print(AnalysisResult result, int maxValuesToShow)
    {
        // ── 摘要面板 ──
        var summaryTable = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn(new TableColumn("[bold]指标[/]").LeftAligned())
            .AddColumn(new TableColumn("[bold]值[/]").RightAligned());

        summaryTable.AddRow("总对象数", $"[aqua]{result.TotalObjects}[/]");
        summaryTable.AddRow("唯一对象数", $"[aqua]{result.TotalUniqueObjects}[/]");
        summaryTable.AddRow("重复组数", result.TotalDuplicateGroups > 0
            ? $"[yellow]{result.TotalDuplicateGroups}[/]"
            : "[green]0[/]");
        summaryTable.AddRow("字段数", $"[aqua]{result.FieldStatistics.Count}[/]");

        AnsiConsole.Write(new Panel(summaryTable)
            .Header("[bold green]分析摘要报告[/]", Justify.Center)
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Green));

        // ── 重复对象 ──
        if (result.TotalDuplicateGroups > 0)
        {
            AnsiConsole.MarkupLine($"[yellow]━━━ 重复对象统计 ({result.TotalDuplicateGroups} 组) ━━━[/]");
            foreach (var dup in result.DuplicateObjects.Take(3))
            {
                AnsiConsole.MarkupLine($"  [grey]•[/] 重复 [yellow]{dup.Count}[/] 次: {Markup.Escape(dup.ObjectJson)}");
            }
            if (result.TotalDuplicateGroups > result.DuplicateObjects.Count)
            {
                AnsiConsole.MarkupLine($"  [grey]... 还有 {result.TotalDuplicateGroups - result.DuplicateObjects.Count} 组未列出（完整 JSON 已省略）[/]");
            }
            else if (result.DuplicateObjects.Count > 3)
            {
                AnsiConsole.MarkupLine($"  [grey]... 还有 {result.DuplicateObjects.Count - 3} 组未列出[/]");
            }
            AnsiConsole.WriteLine();
        }

        // ── 字段详情 ──
        foreach (var stat in result.FieldStatistics)
        {
            // 字段标题行
            string semanticBadge = !string.IsNullOrEmpty(stat.SemanticType)
                ? $" [bold purple][[语义: {stat.SemanticType}]][/]"
                : "";
            AnsiConsole.Write(new Rule($"[bold cyan]{Markup.Escape(stat.FieldName)}[/]{semanticBadge}").LeftJustified());

            // 覆盖率 + 类型
            string defaultInfo = stat.DefaultValueRepresentation != null
                ? $" → 默认值: [green]{Markup.Escape(stat.DefaultValueRepresentation)}[/]"
                : "";
            string ruleInfo = stat.AppliedRule != null ? " [grey][[规则已应用]][/]" : "";
            string truncatedInfo = stat.ValueCountsTruncated ? " [orange1][[唯一值已截断]][/]" : "";
            string types = string.Join(", ", stat.TypeCounts.Select(t => $"[blue]{t.Key}[/]({t.Value})"));
            AnsiConsole.MarkupLine($"  覆盖率: [aqua]{stat.Count}/{result.TotalObjects}[/] ([yellow]{stat.Coverage * 100:F1}%[/]){defaultInfo}{ruleInfo}{truncatedInfo}");
            AnsiConsole.MarkupLine($"  类型分布: {types}");

            // 数值极值与长度指标
            if (stat.MinNumber.HasValue && stat.MaxNumber.HasValue)
            {
                AnsiConsole.MarkupLine($"  数值范围: [magenta]{stat.MinNumber:G5}[/] ~ [magenta]{stat.MaxNumber:G5}[/] (均值: [magenta]{stat.AvgNumber:G5}[/])");
            }
            if (stat.MinLength.HasValue && stat.MaxLength.HasValue)
            {
                AnsiConsole.MarkupLine($"  文本长度: [grey]{stat.MinLength}[/] ~ [grey]{stat.MaxLength}[/] 字符");
            }

            // 枚举违规值
            if (stat.EnumViolations.Count > 0)
            {
                var violations = string.Join(", ", stat.EnumViolations.Select(v => $"[red]{Markup.Escape(v)}[/]"));
                AnsiConsole.MarkupLine($"  [red]枚举违规:[/] {violations}");
            }

            // 值频次表格
            var valueTable = new Table()
                .Border(TableBorder.Simple)
                .BorderColor(Color.Grey)
                .AddColumn(new TableColumn("[bold]值[/]").LeftAligned())
                .AddColumn(new TableColumn("[bold]次数[/]").RightAligned())
                .AddColumn(new TableColumn("[bold]占比[/]").RightAligned());

            var sortedValues = stat.ValueCounts.OrderByDescending(v => v.Value).ToList();
            int shown = 0;
            foreach (var val in sortedValues)
            {
                if (shown >= maxValuesToShow)
                {
                    int remaining = sortedValues.Count - maxValuesToShow;
                    valueTable.AddRow($"[grey]... 还有 {remaining} 个唯一值[/]", "", "");
                    break;
                }

                string displayVal = val.Key;
                if (displayVal.Length > 50)
                    displayVal = string.Concat(displayVal.AsSpan(0, 47), "...");
                displayVal = displayVal.Replace("\r", "\\r").Replace("\n", "\\n");

                bool isDefault = stat.DefaultValueRepresentation != null &&
                    string.Equals(val.Key, stat.DefaultValueRepresentation, StringComparison.OrdinalIgnoreCase);

                string valMarkup = isDefault
                    ? $"[green]{Markup.Escape(displayVal)} [bold][[默认值]][/][/] " // Fix #7: was Unicode-escaped \u9ed8\u8ba4\u503c
                    : Markup.Escape(displayVal);

                double pct = stat.Count > 0 ? (double)val.Value / stat.Count * 100 : 0;
                valueTable.AddRow(valMarkup, $"[aqua]{val.Value}[/]", $"[yellow]{pct:F1}%[/]");
                shown++;
            }

            AnsiConsole.Write(valueTable);
            AnsiConsole.WriteLine();
        }
    }
}
