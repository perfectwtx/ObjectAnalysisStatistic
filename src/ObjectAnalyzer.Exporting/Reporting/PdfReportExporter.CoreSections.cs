using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace ObjectAnalyzer.Reporting;

public static partial class PdfReportExporter
{
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
                    var top3 = string.Join("  ·  ", ResultProjection.Values(s).Take(3).Select(v => $"{v.Display}×{v.Count}"));
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

    private static string Truncate(string s, int maxLen)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= maxLen ? s : string.Concat(s.AsSpan(0, maxLen), "…");
    }
}
