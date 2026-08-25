using System;
using System.Linq;
using Spectre.Console;

namespace ObjectAnalyzer.Reporting;

public static class ConsoleReporter
{
    public static void Print(AnalysisResult result, int maxValuesToShow, bool redactPii = true)
    {
        result = PiiRedactor.Redact(result, redactPii);

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
            string transformInfo = "";
            if (stat.TransformType != null)
            {
                transformInfo = $" [purple][[转换: {Markup.Escape(stat.TransformType)}]]";
                if (stat.TransformFailureCount > 0)
                {
                    transformInfo += $" 失败 {stat.TransformFailureCount} 次";
                }
                transformInfo += "[/]";
            }
            string truncatedInfo = stat.ValueCountsTruncated ? " [orange1][[唯一值已截断]][/]" : "";
            string types = string.Join(", ", stat.TypeCounts.Select(t => $"[blue]{t.Key}[/]({t.Value})"));
            AnsiConsole.MarkupLine($"  覆盖率: [aqua]{stat.Count}/{result.TotalObjects}[/] ([yellow]{stat.Coverage * 100:F1}%[/]){defaultInfo}{ruleInfo}{transformInfo}{truncatedInfo}");
            AnsiConsole.MarkupLine($"  类型分布: {types}");

            // 数值极值与长度指标
            if (stat.MinNumber.HasValue && stat.MaxNumber.HasValue)
            {
                string stdDevInfo = stat.StdDevNumber.HasValue ? $", 标准差: [magenta]{stat.StdDevNumber:G5}[/]" : "";
                AnsiConsole.MarkupLine($"  数值范围: [magenta]{stat.MinNumber:G5}[/] ~ [magenta]{stat.MaxNumber:G5}[/] (均值: [magenta]{stat.AvgNumber:G5}[/]{stdDevInfo})");

                if (stat.FormatQuantiles() is { } quantiles)
                {
                    AnsiConsole.MarkupLine($"  分位数: [cyan]{Markup.Escape(quantiles)}[/]");
                }

                if (stat.Histogram is { Count: > 0 })
                {
                    // 直方图按计数降序取前几桶，避免窄屏溢出
                    var topBuckets = stat.Histogram
                        .OrderByDescending(b => b.Count)
                        .Take(6)
                        .OrderBy(b => b.From);
                    string histogram = string.Join("  ", topBuckets.Select(b => $"{b}×{b.Count}"));
                    AnsiConsole.MarkupLine($"  直方图: [grey]{Markup.Escape(histogram)}[/]");
                }

                // 异常值（IQR 1.5×）
                if (stat.OutlierCount.HasValue && stat.OutlierRatio.HasValue)
                {
                    string ratioColor = stat.OutlierRatio.Value > 0.1 ? "orange1" : "grey";
                    AnsiConsole.MarkupLine(
                        $"  异常值: [yellow]{stat.OutlierCount}[/] 个 ({Markup.Escape($"[{ratioColor}]{stat.OutlierRatio:P1}[/]")})，区间 [cyan]{Markup.Escape(stat.OutlierBounds ?? "-")}[/] ({stat.OutlierMethod} 近似)");
                }
            }
            if (stat.MinLength.HasValue && stat.MaxLength.HasValue)
            {
                string avgLenInfo = stat.AvgLength.HasValue ? $" (平均: [grey]{stat.AvgLength:G4}[/])" : "";
                AnsiConsole.MarkupLine($"  文本长度: [grey]{stat.MinLength}[/] ~ [grey]{stat.MaxLength}[/] 字符{avgLenInfo}");
            }

            // 日期/时间深度剖析
            if (stat.MinDateTime.HasValue && stat.MaxDateTime.HasValue)
            {
                AnsiConsole.MarkupLine(
                    $"  时间范围: [magenta]{stat.MinDateTime:yyyy-MM-dd HH:mm:ss}[/] ~ [magenta]{stat.MaxDateTime:yyyy-MM-dd HH:mm:ss}[/] UTC (样本 [aqua]{stat.DateTimeCount}[/])");

                if (stat.DateHistogram is { Count: > 0 })
                {
                    var topBuckets = stat.DateHistogram
                        .OrderByDescending(b => b.Count)
                        .Take(6)
                        .OrderBy(b => b.From);
                    string histLine = string.Join("  ", topBuckets.Select(b => $"{b}×{b.Count}"));
                    AnsiConsole.MarkupLine($"  时间直方图: [grey]{Markup.Escape(histLine)}[/]");
                }
            }

            if (stat.DistinctCount.HasValue)
            {
                string distinctNote = stat.DistinctApproximate ? " (HyperLogLog 近似)" : "";
                AnsiConsole.MarkupLine($"  唯一值: [teal]{stat.DistinctCount}[/][grey]{distinctNote}[/]");
            }

            // 枚举违规值
            if (stat.EnumViolations.Count > 0)
            {
                var violations = string.Join(", ", stat.EnumViolations.Select(v => $"[red]{Markup.Escape(v)}[/]"));
                AnsiConsole.MarkupLine($"  [red]枚举违规:[/] {violations}");
            }

            // 质量检查 v2：范围/模式/必填/唯一/null 率违规条目（枚举聚合条目已由上方逐值列表覆盖）
            foreach (var check in result.QualityViolations)
            {
                if (check.Check == QualityCheckTypes.EnumValues ||
                    !string.Equals(check.Field, stat.FieldName, StringComparison.Ordinal))
                    continue;
                string approxMark = check.Approximate ? " [grey](近似)[/]" : "";
                string samples = check.Samples.Count > 0
                    ? $" [grey]样本:[/] {string.Join(", ", check.Samples.Select(s => $"[red]{Markup.Escape(s)}[/]"))}"
                    : "";
                AnsiConsole.MarkupLine(
                    $"  [red]{Markup.Escape(QualityCheckTypes.Label(check.Check))}:[/] {Markup.Escape(check.Message)}{approxMark}{samples}");
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

        // ── Schema 演进（diff） ──
        if (result.SchemaDiff != null)
        {
            AnsiConsole.Write(new Rule("[bold yellow]Schema 演进（vs 基线）[/]").LeftJustified());
            SchemaDiffer.PrintSummary(result.SchemaDiff);
            AnsiConsole.WriteLine();
        }
    }
}
