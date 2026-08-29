using System;
using System.Globalization;
using System.IO;
using System.Linq;
using ObjectAnalyzer.Analysis;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Spectre.Console;

namespace ObjectAnalyzer.Reporting;

/// <summary>
/// 将分析结果导出为 PDF 报告（QuestPDF 结构化排版）。覆盖字段统计、重复对象、
/// 违规清单及 P4 七项分析。适合交付给非技术受众（管理层 / 合规审计 / 业务方）。
/// </summary>
public static class PdfReportExporter
{
    static PdfReportExporter()
    {
        // QuestPDF 社区许可（年收入 &lt; 100 万美元 / 非企业可免费使用）
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public static void Save(AnalysisResult result, string outputPath)
    {
        AnsiConsole.MarkupLine($"正在将 PDF 报告保存至: [blue]{Markup.Escape(outputPath)}[/] ...");

        var doc = Document.Create(c => Compose(c, result));
        doc.GeneratePdf(outputPath);
        AnsiConsole.MarkupLine($"[green]PDF 报告成功保存！[/]");
    }

    // ────── 整体文档结构 ──────

    private static void Compose(IDocumentContainer c, AnalysisResult r)
    {
        c.Page(p =>
        {
            p.Size(PageSizes.A4);
            p.Margin(2, Unit.Centimetre);
            p.PageColor(Colors.White);
            p.DefaultTextStyle(t => t.FontSize(10).FontFamily(Fonts.Calibri));

            p.Header().Element(h => Header(h, r));
            p.Content().Element(b => Body(b, r));
            p.Footer().AlignCenter().Text(t =>
            {
                t.DefaultTextStyle(s => s.FontSize(8).FontColor(Colors.Grey.Medium));
                t.Span("ObjectAnalyzer · 第 ");
                t.CurrentPageNumber();
                t.Span(" 页 / 共 ");
                t.TotalPages();
                t.Span(" 页");
            });
        });
    }

    private static void Header(IContainer container, AnalysisResult r)
    {
        container.Column(col =>
        {
            col.Item().Text("对象分析报告").FontSize(20).Bold().FontColor(Colors.Blue.Darken3);
            col.Item().Text(t =>
            {
                t.DefaultTextStyle(s => s.FontSize(9).FontColor(Colors.Grey.Darken1));
                t.Span($"生成时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}   ·   ");
                t.Span($"被分析对象：{r.TotalObjects:N0}   ·   ");
                t.Span($"字段数：{r.FieldStatistics.Count}");
                if (!string.IsNullOrEmpty(r.FilterExpression))
                    t.Span($"   ·   过滤：{r.FilterExpression}");
            });
            col.Item().PaddingTop(4).LineHorizontal(0.6f).LineColor(Colors.Grey.Lighten1);
        });
    }

    private static void Body(IContainer container, AnalysisResult r)
    {
        container.PaddingVertical(8).Column(col =>
        {
            col.Spacing(12);
            col.Item().Element(c => SummarySection(c, r));
            col.Item().Element(c => FieldStatsSection(c, r));
            col.Item().Element(c => ViolationsSection(c, r));
            col.Item().Element(c => DuplicatesSection(c, r));

            // P4 sections (conditional)
            if (r.StringPatterns is { Count: > 0 })
                col.Item().Element(c => StringPatternsSection(c, r));
            if (r.UnicodeHygiene is { Count: > 0 })
                col.Item().Element(c => UnicodeHygieneSection(c, r));
            if (r.FuzzyDuplicates is { Groups.Count: > 0 })
                col.Item().Element(c => FuzzyDedupSection(c, r));
            if (r.CorrelationMatrix is { Fields.Count: > 0 })
                col.Item().Element(c => CorrelationSection(c, r));
            if (r.TimeSeriesAnalyses is { Count: > 0 })
                col.Item().Element(c => TimeSeriesSection(c, r));
            if (r.DistributionDiff is not null)
                col.Item().Element(c => DistributionDiffSection(c, r));
            if (r.SamplingConfidence is not null)
                col.Item().Element(c => SamplingConfidenceSection(c, r));
        });
    }

    // ────── 共用辅助 ──────

    private static void SectionTitle(IContainer container, string title, string? subtitle = null)
    {
        container.Column(col =>
        {
            col.Item().Text(title).FontSize(14).Bold().FontColor(Colors.Blue.Darken2);
            if (!string.IsNullOrEmpty(subtitle))
                col.Item().Text(subtitle).FontSize(9).FontColor(Colors.Grey.Darken1);
            col.Item().PaddingTop(2);
        });
    }

    private static string Fmt(double? v, string fmt = "F3") => v?.ToString(fmt, CultureInfo.InvariantCulture) ?? "—";
    private static string Pct(double? v) => v?.ToString("P1", CultureInfo.InvariantCulture) ?? "—";
    private static string N(long v) => v.ToString("N0", CultureInfo.InvariantCulture);

    // ────── 摘要 ──────

    private static void SummarySection(IContainer container, AnalysisResult r)
    {
        container.Column(col =>
        {
            col.Item().Element(c => SectionTitle(c, "1. 摘要"));
            col.Item().Table(t =>
            {
                t.ColumnsDefinition(cd => { cd.RelativeColumn(2); cd.RelativeColumn(3); });
                t.Header(h => h.Cell().ColumnSpan(2).Background(Colors.Grey.Lighten3).Padding(4).Text("本次分析概览").Bold());
                SummaryRow(t, "被分析对象总数", N(r.TotalObjects));
                SummaryRow(t, "去重后对象数", N(r.TotalUniqueObjects));
                SummaryRow(t, "重复对象组数", N(r.TotalDuplicateGroups));
                SummaryRow(t, "字段总数", N(r.FieldStatistics.Count));
                SummaryRow(t, "质量违规字段数", N(r.QualityViolations.Select(v => v.Field).Distinct().Count()));
                SummaryRow(t, "质量违规总数", N(r.QualityViolations.Count));
            });
        });
    }

    private static void SummaryRow(QuestPDF.Fluent.TableDescriptor t, string k, string v)
    {
        t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3).Text(k);
        t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3).Text(v);
    }

    // ────── 字段统计 ──────

    private static void FieldStatsSection(IContainer container, AnalysisResult r)
    {
        container.Column(col =>
        {
            col.Item().Element(c => SectionTitle(c, "2. 字段统计", "按样本数降序，仅展示前 50 条"));
            col.Item().Table(t =>
            {
                t.ColumnsDefinition(cd =>
                {
                    cd.RelativeColumn(3);
                    cd.RelativeColumn(1);
                    cd.RelativeColumn(1);
                    cd.RelativeColumn(1);
                    cd.RelativeColumn(1);
                    cd.RelativeColumn(2);
                });
                t.Header(h =>
                {
                    h.Cell().Background(Colors.Blue.Darken3).Padding(4).Text("字段").FontColor(Colors.White).Bold();
                    h.Cell().Background(Colors.Blue.Darken3).Padding(4).Text("样本").FontColor(Colors.White).Bold();
                    h.Cell().Background(Colors.Blue.Darken3).Padding(4).Text("覆盖").FontColor(Colors.White).Bold();
                    h.Cell().Background(Colors.Blue.Darken3).Padding(4).Text("主导类型").FontColor(Colors.White).Bold();
                    h.Cell().Background(Colors.Blue.Darken3).Padding(4).Text("均值 / 总和").FontColor(Colors.White).Bold();
                    h.Cell().Background(Colors.Blue.Darken3).Padding(4).Text("Top 3 值").FontColor(Colors.White).Bold();
                });
                foreach (var s in r.FieldStatistics.OrderByDescending(s => s.Count).Take(50))
                {
                    t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3).Text(s.FieldName);
                    t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3).Text(N(s.Count));
                    t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3).Text(Pct(s.Coverage));
                    t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3).Text(s.PrimaryType);
                    string avg = s.MinNumber.HasValue ? $"{Fmt(s.AvgNumber)} / {Fmt(s.SumNumber, "G4")}" : "—";
                    t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3).Text(avg);
                    var top3 = string.Join("  ·  ", s.ValueCounts.OrderByDescending(v => v.Value).Take(3).Select(v => $"{v.Key}×{v.Value}"));
                    t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3).Text(top3).FontSize(8);
                }
            });
        });
    }

    // ────── 违规清单 ──────

    private static void ViolationsSection(IContainer container, AnalysisResult r)
    {
        if (r.QualityViolations.Count == 0) return;
        container.Column(col =>
        {
            col.Item().Element(c => SectionTitle(c, "3. 质量违规清单", $"共 {r.QualityViolations.Count} 条"));
            col.Item().Table(t =>
            {
                t.ColumnsDefinition(cd =>
                {
                    cd.RelativeColumn(3);
                    cd.RelativeColumn(2);
                    cd.RelativeColumn(1);
                    cd.RelativeColumn(4);
                });
                t.Header(h =>
                {
                    h.Cell().Background(Colors.Red.Darken2).Padding(4).Text("字段").FontColor(Colors.White).Bold();
                    h.Cell().Background(Colors.Red.Darken2).Padding(4).Text("规则").FontColor(Colors.White).Bold();
                    h.Cell().Background(Colors.Red.Darken2).Padding(4).Text("次数").FontColor(Colors.White).Bold();
                    h.Cell().Background(Colors.Red.Darken2).Padding(4).Text("说明").FontColor(Colors.White).Bold();
                });
                foreach (var v in r.QualityViolations.Take(50))
                {
                    t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3).Text(v.Field);
                    t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3).Text(v.Check);
                    t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3).Text(N(v.Count));
                    t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3).Text(v.Message);
                }
            });
        });
    }

    // ────── 重复对象 ──────

    private static void DuplicatesSection(IContainer container, AnalysisResult r)
    {
        if (r.DuplicateObjects.Count == 0) return;
        container.Column(col =>
        {
            col.Item().Element(c => SectionTitle(c, "4. 重复对象", $"共 {r.TotalDuplicateGroups} 组"));
            col.Item().Table(t =>
            {
                t.ColumnsDefinition(cd => { cd.RelativeColumn(1); cd.RelativeColumn(5); });
                t.Header(h =>
                {
                    h.Cell().Background(Colors.Orange.Darken2).Padding(4).Text("重复次数").FontColor(Colors.White).Bold();
                    h.Cell().Background(Colors.Orange.Darken2).Padding(4).Text("对象内容").FontColor(Colors.White).Bold();
                });
                foreach (var d in r.DuplicateObjects.Take(20))
                {
                    t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3).Text(N(d.Count));
                    t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3).Text(Truncate(d.ObjectJson, 200)).FontSize(8);
                }
            });
        });
    }

    // ────── P4 通用 ──────

    private static IContainer Badge(IContainer c) => c.Background(Colors.Blue.Lighten4).Padding(2);

    private static void P4Header(IContainer container, string title, string subtitle)
    {
        container.Column(col =>
        {
            col.Item().Text(title).FontSize(14).Bold().FontColor(Colors.Orange.Darken3);
            col.Item().Text(subtitle).FontSize(9).FontColor(Colors.Grey.Darken1);
            col.Item().PaddingTop(2);
        });
    }

    // ────── P4：字符串模式 ──────

    private static void StringPatternsSection(IContainer container, AnalysisResult r)
    {
        container.Column(col =>
        {
            col.Item().Element(c => P4Header(c, "5. 字符串模式归纳", "自动发现形态模板（###-####、身份证号、订单号等）"));
            col.Item().Table(t =>
            {
                t.ColumnsDefinition(cd => { cd.RelativeColumn(3); cd.RelativeColumn(1); cd.RelativeColumn(2); cd.RelativeColumn(4); });
                t.Header(h =>
                {
                    h.Cell().Background(Colors.Orange.Darken2).Padding(4).Text("字段").FontColor(Colors.White).Bold();
                    h.Cell().Background(Colors.Orange.Darken2).Padding(4).Text("样本").FontColor(Colors.White).Bold();
                    h.Cell().Background(Colors.Orange.Darken2).Padding(4).Text("主导模板").FontColor(Colors.White).Bold();
                    h.Cell().Background(Colors.Orange.Darken2).Padding(4).Text("Top 5 模板").FontColor(Colors.White).Bold();
                });
                foreach (var (field, profile) in r.StringPatterns!.OrderByDescending(p => p.Value.TotalSamples).Take(30))
                {
                    var top = string.Join("   ", profile.Patterns.Take(5).Select(t2 =>
                        $"{t2.Template}×{t2.Count} ({(t2.Ratio?.ToString("P0", CultureInfo.InvariantCulture) ?? "—")})"));
                    t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3).Text(field);
                    t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3).Text(N(profile.TotalSamples));
                    t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3).Text(profile.DominantPattern?.Template ?? "—").FontFamily(Fonts.Consolas);
                    t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3).Text(top).FontSize(8);
                }
            });
        });
    }

    // ────── P4：Unicode 卫生 ──────

    private static void UnicodeHygieneSection(IContainer container, AnalysisResult r)
    {
        var dirty = r.UnicodeHygiene!.Where(p => p.Value.HygieneScore is < 90.0).OrderBy(p => p.Value.HygieneScore).Take(20).ToList();
        container.Column(col =>
        {
            col.Item().Element(c => P4Header(c, "6. Unicode 卫生检查", $"评分 < 90 的字段共 {dirty.Count} 个（展示前 20）"));
            col.Item().Table(t =>
            {
                t.ColumnsDefinition(cd => { cd.RelativeColumn(3); cd.RelativeColumn(1); cd.RelativeColumn(2); cd.RelativeColumn(4); });
                t.Header(h =>
                {
                    h.Cell().Background(Colors.Orange.Darken2).Padding(4).Text("字段").FontColor(Colors.White).Bold();
                    h.Cell().Background(Colors.Orange.Darken2).Padding(4).Text("评分").FontColor(Colors.White).Bold();
                    h.Cell().Background(Colors.Orange.Darken2).Padding(4).Text("全角占比").FontColor(Colors.White).Bold();
                    h.Cell().Background(Colors.Orange.Darken2).Padding(4).Text("问题类型").FontColor(Colors.White).Bold();
                });
                foreach (var (field, rep) in dirty)
                {
                    var issues = new System.Collections.Generic.List<string>();
                    if (rep.ZeroWidthCount > 0) issues.Add($"零宽×{rep.ZeroWidthCount}");
                    if (rep.InvisibleCount > 0) issues.Add($"不可见×{rep.InvisibleCount}");
                    if (rep.ControlCharCount > 0) issues.Add($"控制字符×{rep.ControlCharCount}");
                    if (rep.BidiControlCount > 0) issues.Add($"双向控制符×{rep.BidiControlCount}");
                    if (rep.SurroundingWhitespaceCount > 0) issues.Add($"前后空白×{rep.SurroundingWhitespaceCount}");
                    if (rep.MixedCaseCount > 0) issues.Add($"大小写混用×{rep.MixedCaseCount}");
                    string score = rep.HygieneScore?.ToString("F1", CultureInfo.InvariantCulture) ?? "—";
                    string scoreColor = (rep.HygieneScore ?? 100) < 70 ? Colors.Red.Medium : (rep.HygieneScore ?? 100) < 90 ? Colors.Orange.Medium : Colors.Green.Medium;
                    t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3).Text(field);
                    t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3).Text(score).FontColor(scoreColor).Bold();
                    t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3).Text(Pct(rep.FullwidthRatio));
                    t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3).Text(string.Join("   ", issues)).FontSize(8);
                }
            });
        });
    }

    // ────── P4：模糊去重 ──────

    private static void FuzzyDedupSection(IContainer container, AnalysisResult r)
    {
        container.Column(col =>
        {
            col.Item().Element(c => P4Header(c, "7. 模糊去重", $"共 {r.FuzzyDuplicates!.Groups.Count} 组相似重复"));
            col.Item().Table(t =>
            {
                t.ColumnsDefinition(cd => { cd.RelativeColumn(4); cd.RelativeColumn(1); cd.RelativeColumn(2); cd.RelativeColumn(3); });
                t.Header(h =>
                {
                    h.Cell().Background(Colors.Orange.Darken2).Padding(4).Text("规范化键").FontColor(Colors.White).Bold();
                    h.Cell().Background(Colors.Orange.Darken2).Padding(4).Text("组大小").FontColor(Colors.White).Bold();
                    h.Cell().Background(Colors.Orange.Darken2).Padding(4).Text("平均相似度").FontColor(Colors.White).Bold();
                    h.Cell().Background(Colors.Orange.Darken2).Padding(4).Text("样例").FontColor(Colors.White).Bold();
                });
                foreach (var g in r.FuzzyDuplicates!.Groups.Take(20))
                {
                    t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3).Text(Truncate(g.CanonicalKey, 50));
                    t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3).Text(N(g.Count));
                    t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3).Text(g.AverageSimilarity.ToString("F2", CultureInfo.InvariantCulture));
                    t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3).Text(string.Join(" | ", g.Examples.Take(3))).FontSize(8);
                }
            });
        });
    }

    // ────── P4：字段相关性 ──────

    private static void CorrelationSection(IContainer container, AnalysisResult r)
    {
        var cm = r.CorrelationMatrix!;
        container.Column(col =>
        {
            col.Item().Element(c => P4Header(c, "8. 字段相关性矩阵", $"样本 {N(cm.Samples)}，字段 {cm.Fields.Count}，强相关对 {cm.StrongPairs.Count}"));
            if (cm.StrongPairs.Count > 0)
            {
                col.Item().Text("强相关对（|Pearson| ≥ 0.7 且样本 ≥ 30）").Italic();
                col.Item().Table(t =>
                {
                    t.ColumnsDefinition(cd => { cd.RelativeColumn(3); cd.RelativeColumn(3); cd.RelativeColumn(1); cd.RelativeColumn(1); cd.RelativeColumn(1); });
                    t.Header(h =>
                    {
                        h.Cell().Background(Colors.Orange.Darken2).Padding(4).Text("字段 A").FontColor(Colors.White).Bold();
                        h.Cell().Background(Colors.Orange.Darken2).Padding(4).Text("字段 B").FontColor(Colors.White).Bold();
                        h.Cell().Background(Colors.Orange.Darken2).Padding(4).Text("Pearson").FontColor(Colors.White).Bold();
                        h.Cell().Background(Colors.Orange.Darken2).Padding(4).Text("Spearman").FontColor(Colors.White).Bold();
                        h.Cell().Background(Colors.Orange.Darken2).Padding(4).Text("样本").FontColor(Colors.White).Bold();
                    });
                    foreach (var p in cm.StrongPairs.Take(20))
                    {
                        t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3).Text(p.FieldA);
                        t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3).Text(p.FieldB);
                        t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3).Text(Fmt(p.Pearson));
                        t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3).Text(Fmt(p.Spearman));
                        t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3).Text(N(p.Samples));
                    }
                });
            }
            else
            {
                col.Item().Text("（未检测到强相关对）").Italic().FontColor(Colors.Grey.Medium);
            }
        });
    }

    // ────── P4：时间序列 ──────

    private static void TimeSeriesSection(IContainer container, AnalysisResult r)
    {
        container.Column(col =>
        {
            col.Item().Element(c => P4Header(c, "9. 时间序列深度分析", $"覆盖 {r.TimeSeriesAnalyses!.Count} 个 DateTime 字段"));
            col.Item().Table(t =>
            {
                t.ColumnsDefinition(cd => { cd.RelativeColumn(3); cd.RelativeColumn(1); cd.RelativeColumn(1); cd.RelativeColumn(1); cd.RelativeColumn(1); cd.RelativeColumn(1); });
                t.Header(h =>
                {
                    h.Cell().Background(Colors.Orange.Darken2).Padding(4).Text("字段").FontColor(Colors.White).Bold();
                    h.Cell().Background(Colors.Orange.Darken2).Padding(4).Text("趋势").FontColor(Colors.White).Bold();
                    h.Cell().Background(Colors.Orange.Darken2).Padding(4).Text("R²").FontColor(Colors.White).Bold();
                    h.Cell().Background(Colors.Orange.Darken2).Padding(4).Text("周期").FontColor(Colors.White).Bold();
                    h.Cell().Background(Colors.Orange.Darken2).Padding(4).Text("脉冲性").FontColor(Colors.White).Bold();
                    h.Cell().Background(Colors.Orange.Darken2).Padding(4).Text("空桶").FontColor(Colors.White).Bold();
                });
                foreach (var (field, ts) in r.TimeSeriesAnalyses!)
                {
                    string trendColor = ts.Trend switch
                    {
                        "rising" => Colors.Green.Medium,
                        "falling" => Colors.Red.Medium,
                        _ => Colors.Grey.Darken1,
                    };
                    t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3).Text(field);
                    t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3).Text(ts.Trend).FontColor(trendColor).Bold();
                    t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3).Text(Fmt(ts.TrendRSquared, "F2"));
                    t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3).Text(ts.DetectedPeriod?.ToString(CultureInfo.InvariantCulture) ?? "—");
                    t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3).Text(Fmt(ts.Burstiness, "F2"));
                    t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3).Text(Pct(ts.EmptyRatio));
                }
            });
        });
    }

    // ────── P4：分布漂移 ──────

    private static void DistributionDiffSection(IContainer container, AnalysisResult r)
    {
        var d = r.DistributionDiff!;
        container.Column(col =>
        {
            col.Item().Element(c => P4Header(c, "10. 值分布漂移", $"显著漂移字段 {d.SignificantDriftCount} 个"));
            if (d.NumericDrifts.Count > 0)
            {
                col.Item().Text("数值字段漂移").Italic();
                col.Item().Table(t =>
                {
                    t.ColumnsDefinition(cd => { cd.RelativeColumn(3); cd.RelativeColumn(2); cd.RelativeColumn(2); cd.RelativeColumn(2); cd.RelativeColumn(1); });
                    t.Header(h =>
                    {
                        h.Cell().Background(Colors.Orange.Darken2).Padding(4).Text("字段").FontColor(Colors.White).Bold();
                        h.Cell().Background(Colors.Orange.Darken2).Padding(4).Text("基线均值").FontColor(Colors.White).Bold();
                        h.Cell().Background(Colors.Orange.Darken2).Padding(4).Text("当前均值").FontColor(Colors.White).Bold();
                        h.Cell().Background(Colors.Orange.Darken2).Padding(4).Text("偏移").FontColor(Colors.White).Bold();
                        h.Cell().Background(Colors.Orange.Darken2).Padding(4).Text("σ").FontColor(Colors.White).Bold();
                    });
                    foreach (var n in d.NumericDrifts.OrderByDescending(x => x.MeanShiftSigma ?? 0).Take(15))
                    {
                        string sigma = n.MeanShiftSigma?.ToString("F2", CultureInfo.InvariantCulture) ?? "—";
                        string color = (n.MeanShiftSigma ?? 0) >= 2 ? Colors.Red.Medium : (n.MeanShiftSigma ?? 0) >= 1 ? Colors.Orange.Medium : Colors.Grey.Medium;
                        t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3).Text(n.Path);
                        t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3).Text(Fmt(n.BaselineMean));
                        t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3).Text(Fmt(n.CurrentMean));
                        t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3).Text(Fmt(n.MeanShift));
                        t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3).Text(sigma).FontColor(color).Bold();
                    }
                });
            }
            if (d.DistinctDrifts.Count > 0)
            {
                col.Item().PaddingTop(6).Text("类别字段漂移").Italic();
                col.Item().Table(t =>
                {
                    t.ColumnsDefinition(cd => { cd.RelativeColumn(3); cd.RelativeColumn(2); cd.RelativeColumn(2); cd.RelativeColumn(2); });
                    t.Header(h =>
                    {
                        h.Cell().Background(Colors.Orange.Darken2).Padding(4).Text("字段").FontColor(Colors.White).Bold();
                        h.Cell().Background(Colors.Orange.Darken2).Padding(4).Text("基线 distinct").FontColor(Colors.White).Bold();
                        h.Cell().Background(Colors.Orange.Darken2).Padding(4).Text("当前 distinct").FontColor(Colors.White).Bold();
                        h.Cell().Background(Colors.Orange.Darken2).Padding(4).Text("Jaccard").FontColor(Colors.White).Bold();
                    });
                    foreach (var c2 in d.DistinctDrifts.OrderBy(x => x.Jaccard ?? 1).Take(15))
                    {
                        t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3).Text(c2.Path);
                        t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3).Text(c2.BaselineDistinct?.ToString(CultureInfo.InvariantCulture) ?? "—");
                        t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3).Text(c2.CurrentDistinct?.ToString(CultureInfo.InvariantCulture) ?? "—");
                        t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3).Text(Fmt(c2.Jaccard));
                    }
                });
            }
        });
    }

    // ────── P4：采样置信 ──────

    private static void SamplingConfidenceSection(IContainer container, AnalysisResult r)
    {
        var sc = r.SamplingConfidence!;
        container.Column(col =>
        {
            col.Item().Element(c => P4Header(c, "11. 采样置信区间", $"{sc.SamplingMethod} · 有效样本 {N(sc.EffectiveSampleSize)}"));
            col.Item().Table(t =>
            {
                t.ColumnsDefinition(cd => { cd.RelativeColumn(3); cd.RelativeColumn(2); cd.RelativeColumn(2); cd.RelativeColumn(2); cd.RelativeColumn(1); });
                t.Header(h =>
                {
                    h.Cell().Background(Colors.Orange.Darken2).Padding(4).Text("字段").FontColor(Colors.White).Bold();
                    h.Cell().Background(Colors.Orange.Darken2).Padding(4).Text("下限").FontColor(Colors.White).Bold();
                    h.Cell().Background(Colors.Orange.Darken2).Padding(4).Text("中心估计").FontColor(Colors.White).Bold();
                    h.Cell().Background(Colors.Orange.Darken2).Padding(4).Text("上限").FontColor(Colors.White).Bold();
                    h.Cell().Background(Colors.Orange.Darken2).Padding(4).Text("宽度").FontColor(Colors.White).Bold();
                });
                foreach (var (field, ci) in sc.CoverageIntervals.Take(30))
                {
                    string color = ci.Ambiguous ? Colors.Red.Medium : Colors.Black;
                    t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3).Text(field);
                    t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3).Text(Pct(ci.Lower));
                    t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3).Text(Pct(ci.Observed));
                    t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3).Text(Pct(ci.Upper));
                    t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3).Text(Fmt(ci.Width)).FontColor(color);
                }
            });
        });
    }

    private static string Truncate(string s, int maxLen)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= maxLen ? s : string.Concat(s.AsSpan(0, maxLen), "…");
    }
}
