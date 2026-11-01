using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace ObjectAnalyzer.Reporting;

public static partial class PdfReportExporter
{
    // ────── 深度分析通用 ──────

    private static void DeepAnalysisHeader(IContainer container, string title, string subtitle)
    {
        container.Column(col =>
        {
            col.Item().Text(title).FontSize(14).Bold().FontColor(Colors.Orange.Darken3);
            col.Item().Text(subtitle).FontSize(9).FontColor(Colors.Grey.Darken1);
            col.Item().PaddingTop(2);
        });
    }

    // ────── 深度分析：字符串模式 ──────

    private static void StringPatternsSection(IContainer container, AnalysisResult r)
    {
        container.Column(col =>
        {
            col.Item().Element(c => DeepAnalysisHeader(c, "5. 字符串模式归纳", "自动发现形态模板（###-####、身份证号、订单号等）"));
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
                    t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3).Text(profile.DominantPattern?.Template ?? "—").FontFamily("Consolas");
                    t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3).Text(top).FontSize(8);
                }
            });
        });
    }

    // ────── 深度分析：Unicode 卫生 ──────

    private static void UnicodeHygieneSection(IContainer container, AnalysisResult r)
    {
        var dirty = r.UnicodeHygiene!.Where(p => p.Value.HygieneScore is < 90.0).OrderBy(p => p.Value.HygieneScore).Take(20).ToList();
        container.Column(col =>
        {
            col.Item().Element(c => DeepAnalysisHeader(c, "6. Unicode 卫生检查", $"评分 < 90 的字段共 {dirty.Count} 个（展示前 20）"));
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

    // ────── 深度分析：模糊去重 ──────

    private static void FuzzyDedupSection(IContainer container, AnalysisResult r)
    {
        container.Column(col =>
        {
            col.Item().Element(c => DeepAnalysisHeader(c, "7. 模糊去重", $"共 {r.FuzzyDuplicates!.Groups.Count} 组相似重复"));
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

    // ────── 深度分析：字段相关性 ──────

    private static void CorrelationSection(IContainer container, AnalysisResult r)
    {
        var cm = r.CorrelationMatrix!;
        container.Column(col =>
        {
            col.Item().Element(c => DeepAnalysisHeader(c, "8. 字段相关性矩阵", $"样本 {N(cm.Samples)}，字段 {cm.Fields.Count}，强相关对 {cm.StrongPairs.Count}"));
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

    // ────── 深度分析：时间序列 ──────

    private static void TimeSeriesSection(IContainer container, AnalysisResult r)
    {
        container.Column(col =>
        {
            col.Item().Element(c => DeepAnalysisHeader(c, "9. 时间序列深度分析", $"覆盖 {r.TimeSeriesAnalyses!.Count} 个 DateTime 字段"));
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

    // ────── 深度分析：分布漂移 ──────

    private static void DistributionDiffSection(IContainer container, AnalysisResult r)
    {
        var d = r.DistributionDiff!;
        container.Column(col =>
        {
            col.Item().Element(c => DeepAnalysisHeader(c, "10. 值分布漂移", $"显著漂移字段 {d.SignificantDriftCount} 个"));
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

    // ────── 深度分析：采样置信 ──────

    private static void SamplingConfidenceSection(IContainer container, AnalysisResult r)
    {
        var sc = r.SamplingConfidence!;
        container.Column(col =>
        {
            col.Item().Element(c => DeepAnalysisHeader(c, "11. 采样置信区间", $"{sc.SamplingMethod} · 有效样本 {N(sc.EffectiveSampleSize)}"));
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
}
