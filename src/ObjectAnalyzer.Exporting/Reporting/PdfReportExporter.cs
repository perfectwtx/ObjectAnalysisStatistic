using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace ObjectAnalyzer.Reporting;

/// <summary>
/// 将分析结果导出为 PDF 报告（QuestPDF 结构化排版）。覆盖字段统计、重复对象、
/// 违规清单及 深度分析七项。适合交付给非技术受众（管理层 / 合规审计 / 业务方）。
/// </summary>
public static partial class PdfReportExporter
{
    static PdfReportExporter()
    {
        // QuestPDF 社区许可（年收入 &lt; 100 万美元 / 非企业可免费使用）
        QuestPDF.Settings.License = LicenseType.Community;
        // 2026.9 起不再默认使用系统字体；本报告依赖系统 Calibri/Consolas 并提供中文回退，
        // 保持旧版行为：找不到指定字体时静默回退而不是中止生成。
        QuestPDF.Settings.UseSystemFonts = true;
        QuestPDF.Settings.ThrowOnMissingFontFamilies = false;
    }

    public static void Save(AnalysisResult result, string outputPath, bool redactPii = true)
    {
        if (redactPii) result = ResultProjection.Create(result).Result;

        var doc = Document.Create(c => Compose(c, result));
        doc.GeneratePdf(outputPath);
    }

    // ────── 整体文档结构 ──────

    private static void Compose(IDocumentContainer c, AnalysisResult r)
    {
        c.Page(p =>
        {
            p.Size(PageSizes.A4);
            p.Margin(2, Unit.Centimetre);
            p.PageColor(Colors.White);
            p.DefaultTextStyle(t => t.FontSize(10).FontFamily("Calibri"));

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
            if (r.Projection is { Omitted.Count: > 0 } projection)
                col.Item().Text("Delivery omissions: " + string.Join(", ", projection.Omitted));
            if (r.Reliability is { Limitations.Count: > 0 } reliability)
                col.Item().Column(items =>
                {
                    items.Item().Text("Accuracy and limits").Bold();
                    foreach (var item in reliability.Limitations)
                        items.Item().Text($"{item.Metric} ({item.Accuracy}): {item.Reason}");
                });
            col.Item().Element(c => FieldStatsSection(c, r));
            col.Item().Element(c => ViolationsSection(c, r));
            col.Item().Element(c => DuplicatesSection(c, r));

            // Deep analysis sections (conditional)
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
}
