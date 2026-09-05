using ObjectAnalyzer.Abstractions;
using Spectre.Console;

namespace ObjectAnalyzer.Reporting;

/// <summary>
/// CLI 侧的分析结果呈现器：Spectre 富渲染（表格 / 面板 / 图表），实现 Core 的 IResultPresenter 端口。
/// </summary>
public sealed class ConsoleReporter : IResultPresenter
{
    /// <summary>向用户呈现分析结果（默认按 PII 策略打码）。</summary>
    public void Present(AnalysisResult result, AnalysisOptions options) =>
        Print(result, options.Parse.MaxValuesToShow, redactPii: !options.Pii.PiiOff);

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

        // ── 分布漂移（diff）──
        if (result.DistributionDiff != null)
        {
            AnsiConsole.Write(new Rule("[bold yellow]分布漂移（vs 基线）[/]").LeftJustified());
            DistributionDiffer.PrintSummary(result.DistributionDiff);
            AnsiConsole.WriteLine();
        }

        // ── 字段相关性矩阵（Pearson / Spearman）──
        if (result.CorrelationMatrix != null)
        {
            AnsiConsole.Write(new Rule("[bold yellow]字段相关性矩阵[/]").LeftJustified());
            PrintCorrelationMatrix(result.CorrelationMatrix);
            AnsiConsole.WriteLine();
        }

        // ── 字符串模式归纳 ──
        if (result.StringPatterns != null && result.StringPatterns.Count > 0)
        {
            AnsiConsole.Write(new Rule("[bold yellow]字符串模式归纳（自动发现形态模板）[/]").LeftJustified());
            PrintStringPatterns(result.StringPatterns);
            AnsiConsole.WriteLine();
        }

        // ── Unicode 卫生检查 ──
        if (result.UnicodeHygiene != null && result.UnicodeHygiene.Count > 0)
        {
            AnsiConsole.Write(new Rule("[bold yellow]Unicode 卫生检查（中文场景常见脏数据）[/]").LeftJustified());
            PrintUnicodeHygiene(result.UnicodeHygiene);
            AnsiConsole.WriteLine();
        }

        // ── 时间序列深度分析 ──
        if (result.TimeSeriesAnalyses != null && result.TimeSeriesAnalyses.Count > 0)
        {
            AnsiConsole.Write(new Rule("[bold yellow]时间序列深度分析（趋势 / 周期 / 间隙 / 脉冲）[/]").LeftJustified());
            PrintTimeSeries(result.TimeSeriesAnalyses);
            AnsiConsole.WriteLine();
        }

        // ── 模糊去重 ──
        if (result.FuzzyDuplicates != null)
        {
            AnsiConsole.Write(new Rule("[bold yellow]模糊去重（形态变体聚类）[/]").LeftJustified());
            PrintFuzzyDuplicates(result.FuzzyDuplicates);
            AnsiConsole.WriteLine();
        }

        // ── 采样置信区间 ──
        if (result.SamplingConfidence != null)
        {
            AnsiConsole.Write(new Rule("[bold yellow]采样不确定性（95% 置信区间）[/]").LeftJustified());
            PrintSamplingConfidence(result.SamplingConfidence);
            AnsiConsole.WriteLine();
        }
    }

    private static void PrintCorrelationMatrix(FieldCorrelationMatrix matrix)
    {
        if (matrix.Fields.Count == 0)
        {
            AnsiConsole.MarkupLine("  [grey]无字段相关性数据[/]");
            return;
        }
        AnsiConsole.MarkupLine($"  [grey]样本数 {matrix.Samples}（Pearson / Spearman）[/]");
        if (matrix.StrongPairs.Count == 0)
        {
            AnsiConsole.MarkupLine("  [grey]未发现显著相关对（|r| ≥ 0.7 且样本 ≥ 30）[/]");
            return;
        }
        var table = new Table()
            .Border(TableBorder.Simple)
            .BorderColor(Color.Grey)
            .AddColumn(new TableColumn("[bold]字段对[/]").LeftAligned())
            .AddColumn(new TableColumn("[bold]Pearson[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Spearman[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]样本[/]").RightAligned());
        foreach (var pair in matrix.StrongPairs)
        {
            string p = pair.Pearson.HasValue ? $"{pair.Pearson:+0.000;-0.000;0.000}" : "-";
            string s = pair.Spearman.HasValue ? $"{pair.Spearman:+0.000;-0.000;0.000}" : "-";
            string color = pair.Pearson is > 0.7 or < -0.7 ? "yellow" : "grey";
            table.AddRow(
                $"[cyan]{Markup.Escape(pair.FieldA)}[/] × [cyan]{Markup.Escape(pair.FieldB)}[/]",
                $"[{color}]{p}[/]",
                $"[{color}]{s}[/]",
                $"[grey]{pair.Samples}[/]");
        }
        AnsiConsole.Write(table);
    }

    private static void PrintStringPatterns(Dictionary<string, StringPatternProfile> profiles)
    {
        foreach (var (field, profile) in profiles.Take(10))
        {
            string dom = profile.DominantPattern != null
                ? $"[cyan]{Markup.Escape(profile.DominantPattern.Template)}[/] ({profile.DominantPattern.Ratio ?? 0:P1})"
                : "[grey]-[/]";
            AnsiConsole.MarkupLine($"  [cyan]{Markup.Escape(field)}[/]: 主导模板 {dom}；总样本 {profile.TotalSamples}");
            foreach (var p in profile.Patterns.Take(5))
            {
                AnsiConsole.MarkupLine($"    [grey]·[/] [cyan]{Markup.Escape(p.Template)}[/]: {p.Count} ({(p.Ratio ?? 0):P1})");
            }
        }
    }

    private static void PrintUnicodeHygiene(Dictionary<string, UnicodeHygieneReport> reports)
    {
        foreach (var (field, r) in reports.Take(10))
        {
            var flags = new List<string>();
            if (r.FullwidthCount > 0 && r.HalfwidthCount > 0) flags.Add($"全/半角混用 ({r.FullwidthCount}全 / {r.HalfwidthCount}半)");
            if (r.InvisibleCount > 0) flags.Add($"不可见字符 {r.InvisibleCount}");
            if (r.ControlCharCount > 0) flags.Add($"控制字符 {r.ControlCharCount}");
            if (r.SurroundingWhitespaceCount > 0) flags.Add($"首尾空白 {r.SurroundingWhitespaceCount}");
            if (r.MixedCaseCount > 0) flags.Add($"大小写混用 {r.MixedCaseCount}");
            string flagStr = flags.Count > 0 ? string.Join("; ", flags) : "[green]✓ 干净[/]";
            string score = r.HygieneScore.HasValue ? $" 评分 {r.HygieneScore:F0}/100" : "";
            AnsiConsole.MarkupLine($"  [cyan]{Markup.Escape(field)}[/]: 样本 {r.TotalSamples} — {flagStr}{score}");
        }
    }

    private static void PrintTimeSeries(Dictionary<string, TimeSeriesAnalysis> analyses)
    {
        foreach (var (field, ts) in analyses.Take(10))
        {
            string trendColor = ts.Trend == "rising" ? "yellow" : ts.Trend == "falling" ? "red" : "green";
            string period = ts.DetectedPeriod.HasValue ? $"周期={ts.DetectedPeriod}({ts.PeriodicityStrength:P0})" : "无明显周期";
            AnsiConsole.MarkupLine(
                $"  [cyan]{Markup.Escape(field)}[/]: 趋势 [{trendColor}]{ts.Trend}[/] (slope {ts.TrendSlope:F3}/桶)，{period}，间隙 {ts.LongestGapBuckets} 桶，脉冲 {ts.Burstiness:F1}x");
        }
    }

    private static void PrintFuzzyDuplicates(FuzzyDuplicateReport report)
    {
        AnsiConsole.MarkupLine(
            $"  总样本 {report.TotalSamples}，相似重复组 {report.GroupCount}，涉及样本 {report.SimilarDuplicates}");
        foreach (var g in report.Groups.Take(5))
        {
            AnsiConsole.MarkupLine($"    [yellow]·[/] 规范化键 [cyan]{Markup.Escape(g.CanonicalKey)}[/] 样本 {g.Count}");
            foreach (var ex in g.Examples.Take(3))
            {
                AnsiConsole.MarkupLine($"        [grey]- {Markup.Escape(ex)}[/]");
            }
        }
    }

    private static void PrintSamplingConfidence(SamplingConfidence sc)
    {
        AnsiConsole.MarkupLine($"  {Markup.Escape(sc.Summary)}");
        if (sc.LowConfidenceFields.Count > 0)
        {
            AnsiConsole.MarkupLine($"  [yellow]低可信字段:[/] {string.Join(", ", sc.LowConfidenceFields.Take(10).Select(f => $"[cyan]{Markup.Escape(f)}[/]"))}");
        }
    }
}
