using System.Globalization;

namespace ObjectAnalyzer.Reporting;

public static partial class MarkdownReportExporter
{
    private static void WriteSamplingConfidenceSection(TextWriter writer, AnalysisResult result)
    {
        var sc = result.SamplingConfidence;
        if (sc == null) return;
        writer.WriteLine("## 采样不确定性 (Wilson 95% 置信区间)");
        writer.WriteLine();
        writer.WriteLine("> " + sc.Summary);
        writer.WriteLine();
        writer.WriteLine("| 字段 | 观测覆盖 | 区间下界 | 区间上界 | 区间宽度 | 状态 |");
        writer.WriteLine("|------|----------|----------|----------|----------|------|");
        foreach (var (field, ci) in sc.CoverageIntervals.OrderByDescending(p => p.Value.Width))
        {
            bool isLow = sc.LowConfidenceFields.Contains(field);
            string status = ci.Ambiguous ? "**含义模糊**" : isLow ? "**低可信**" : "可信";
            writer.Write("| `"); writer.Write(EscapeMd(field)); writer.Write("` | ");
            writer.Write(ci.Observed.ToString("P1", CultureInfo.InvariantCulture)); writer.Write(" | ");
            writer.Write(ci.Lower.ToString("P1", CultureInfo.InvariantCulture)); writer.Write(" | ");
            writer.Write(ci.Upper.ToString("P1", CultureInfo.InvariantCulture)); writer.Write(" | ");
            writer.Write(ci.Width.ToString("P3", CultureInfo.InvariantCulture)); writer.Write(" | ");
            writer.Write(status); writer.WriteLine(" |");
        }
        writer.WriteLine();
        if (sc.LowConfidenceFields.Count > 0)
        {
            writer.Write("> ⚠️ **低可信字段**: ");
            writer.Write(string.Join(", ", sc.LowConfidenceFields.Select(f => $"`{EscapeMd(f)}`")));
            writer.WriteLine();
        }
        writer.WriteLine();
    }

    private static void WriteCorrelationSection(TextWriter writer, AnalysisResult result)
    {
        var cm = result.CorrelationMatrix;
        if (cm == null || cm.Fields.Count == 0) return;
        writer.WriteLine("## 字段相关性矩阵");
        writer.WriteLine();
        writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"- 样本数: {cm.Samples}；参与字段: {cm.Fields.Count}"));
        writer.WriteLine();

        if (cm.StrongPairs.Count > 0)
        {
            writer.WriteLine("### 强相关对 (|Pearson| ≥ 0.7, 样本 ≥ 30)");
            writer.WriteLine();
            writer.WriteLine("| 字段 A | 字段 B | Pearson | Spearman | 样本 |");
            writer.WriteLine("|--------|--------|---------|----------|------|");
            foreach (var p in cm.StrongPairs)
            {
                writer.Write("| `"); writer.Write(EscapeMd(p.FieldA)); writer.Write("` | `");
                writer.Write(EscapeMd(p.FieldB)); writer.Write("` | ");
                writer.Write(p.Pearson?.ToString("F3", CultureInfo.InvariantCulture) ?? "—"); writer.Write(" | ");
                writer.Write(p.Spearman?.ToString("F3", CultureInfo.InvariantCulture) ?? "—"); writer.Write(" | ");
                writer.Write(p.Samples.ToString(CultureInfo.InvariantCulture));
                writer.WriteLine(" |");
            }
            writer.WriteLine();
        }
    }

    private static void WriteFuzzyDedupSection(TextWriter writer, AnalysisResult result)
    {
        var fd = result.FuzzyDuplicates;
        if (fd == null) return;
        writer.WriteLine("## 模糊去重 (相似重复检测)");
        writer.WriteLine();
        double ratio = fd.TotalSamples > 0 ? (double)fd.SimilarDuplicates / fd.TotalSamples : 0;
        writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"- 总样本: {fd.TotalSamples}，相似重复组: **{fd.GroupCount}**，相似重复样本: {fd.SimilarDuplicates} ({ratio:P1})"));
        writer.WriteLine();
        if (fd.Groups.Count > 0)
        {
            writer.WriteLine("| 代表样本 | 规范化键 | 样本数 | 平均相似度 |");
            writer.WriteLine("|---------|---------|--------|------------|");
            foreach (var g in fd.Groups.Take(20))
            {
                writer.Write("| `"); writer.Write(EscapeMd(Truncate(g.RepresentativeJson, 50))); writer.Write("` | `");
                writer.Write(EscapeMd(g.CanonicalKey)); writer.Write("` | ");
                writer.Write(g.Count.ToString(CultureInfo.InvariantCulture)); writer.Write(" | ");
                writer.Write(g.AverageSimilarity.ToString("F2", CultureInfo.InvariantCulture));
                writer.WriteLine(" |");
            }
            writer.WriteLine();
        }
    }

    private static void WriteStringPatternsSection(TextWriter writer, AnalysisResult result)
    {
        if (result.StringPatterns == null || result.StringPatterns.Count == 0) return;
        writer.WriteLine("## 字符串模式归纳");
        writer.WriteLine();
        writer.WriteLine("| 字段 | 主导模板 | 样本数 | Top 5 模板 |");
        writer.WriteLine("|------|---------|--------|------------|");
        foreach (var (field, profile) in result.StringPatterns.OrderByDescending(p => p.Value.TotalSamples).Take(20))
        {
            string dominant = profile.DominantPattern != null ? $"`{EscapeMd(profile.DominantPattern.Template)}` ×{profile.DominantPattern.Count}" : "—";
            string top = string.Join("<br>", profile.Patterns.Take(5).Select(t =>
            {
                string pct = t.Ratio.HasValue ? t.Ratio.Value.ToString("P0", CultureInfo.InvariantCulture) : "—";
                return $"`{EscapeMd(t.Template)}` ×{t.Count} ({pct})";
            }));
            writer.Write("| `"); writer.Write(EscapeMd(field)); writer.Write("` | ");
            writer.Write(dominant); writer.Write(" | ");
            writer.Write(profile.TotalSamples.ToString(CultureInfo.InvariantCulture)); writer.Write(" | ");
            writer.Write(top);
            writer.WriteLine(" |");
        }
        writer.WriteLine();
    }

    private static void WriteUnicodeHygieneSection(TextWriter writer, AnalysisResult result)
    {
        if (result.UnicodeHygiene == null || result.UnicodeHygiene.Count == 0) return;
        writer.WriteLine("## Unicode 卫生检查 (中文场景脏数据)");
        writer.WriteLine();
        writer.WriteLine("| 字段 | 样本 | 全/半角混用 | 不可见字符 | 控制字符 | 零宽 | 前后空白 | 大小写混用 | 卫生评分 |");
        writer.WriteLine("|------|------|------------|----------|---------|------|----------|------------|----------|");
        foreach (var (field, r) in result.UnicodeHygiene.OrderBy(p => p.Value.HygieneScore ?? 100.0).Take(15))
        {
            string mixed = r.FullwidthCount > 0 && r.HalfwidthCount > 0
                ? $"**{r.FullwidthCount}全/{r.HalfwidthCount}半**" : "—";
            string invisible = r.InvisibleCount > 0 ? $"**{r.InvisibleCount}**" : "—";
            string control = r.ControlCharCount > 0 ? $"**{r.ControlCharCount}**" : "—";
            string zeroWidth = r.ZeroWidthCount > 0 ? $"**{r.ZeroWidthCount}**" : "—";
            string surroundWs = r.SurroundingWhitespaceCount > 0 ? $"**{r.SurroundingWhitespaceCount}**" : "—";
            string mixedCase = r.MixedCaseCount > 0 ? $"**{r.MixedCaseCount}**" : "—";
            string score = r.HygieneScore.HasValue ? r.HygieneScore.Value.ToString("F0", CultureInfo.InvariantCulture) : "—";
            writer.Write("| `"); writer.Write(EscapeMd(field)); writer.Write("` | ");
            writer.Write(r.TotalSamples.ToString(CultureInfo.InvariantCulture)); writer.Write(" | ");
            writer.Write(mixed); writer.Write(" | "); writer.Write(invisible); writer.Write(" | ");
            writer.Write(control); writer.Write(" | "); writer.Write(zeroWidth); writer.Write(" | ");
            writer.Write(surroundWs); writer.Write(" | "); writer.Write(mixedCase); writer.Write(" | ");
            writer.Write(score);
            writer.WriteLine(" |");
        }
        writer.WriteLine();
        writer.WriteLine("> 卫生评分 0-100：综合全角混用、不可见字符、控制字符、大小写混用，越高越干净。仅展示评分最低的 15 个字段。");
        writer.WriteLine();
    }

    private static void WriteTimeSeriesSection(TextWriter writer, AnalysisResult result)
    {
        if (result.TimeSeriesAnalyses == null || result.TimeSeriesAnalyses.Count == 0) return;
        writer.WriteLine("## 时间序列深度分析");
        writer.WriteLine();
        writer.WriteLine("| 字段 | 时间窗 | 桶跨度 | 桶数 | 趋势 | 斜率 | 周期 (桶) | 空桶占比 | 脉冲性 |");
        writer.WriteLine("|------|--------|--------|------|------|------|-----------|---------|--------|");
        foreach (var (field, ts) in result.TimeSeriesAnalyses)
        {
            string period = ts.DetectedPeriod.HasValue
                ? $"{ts.DetectedPeriod} (强度 {ts.PeriodicityStrength?.ToString("F2", CultureInfo.InvariantCulture)})"
                : "—";
            string emptyRatio = ts.EmptyRatio > 0.5 ? $"**{ts.EmptyRatio:P0}**" : ts.EmptyRatio.ToString("P0", CultureInfo.InvariantCulture);
            string burst = ts.Burstiness >= 3 ? $"**{ts.Burstiness:F2}**" : ts.Burstiness.ToString("F2", CultureInfo.InvariantCulture);
            writer.Write("| `"); writer.Write(EscapeMd(field)); writer.Write("` | ");
            writer.Write($"{ts.Start:yyyy-MM-dd} ~ {ts.End:yyyy-MM-dd}"); writer.Write(" | ");
            writer.Write(EscapeMd(ts.BucketSpan.ToString())); writer.Write(" | ");
            writer.Write(ts.BucketCount.ToString(CultureInfo.InvariantCulture)); writer.Write(" | ");
            writer.Write(EscapeMd(ts.Trend)); writer.Write(" | ");
            writer.Write(ts.TrendSlope?.ToString("F3", CultureInfo.InvariantCulture) ?? "—"); writer.Write(" | ");
            writer.Write(period); writer.Write(" | "); writer.Write(emptyRatio); writer.Write(" | ");
            writer.Write(burst);
            writer.WriteLine(" |");
        }
        writer.WriteLine();
    }

    private static void WriteDistributionDiffSection(TextWriter writer, AnalysisResult result)
    {
        var diff = result.DistributionDiff;
        if (diff == null) return;
        writer.WriteLine("## 值分布漂移 (vs 草图基线)");
        writer.WriteLine();
        writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"- 基线: `{EscapeMd(diff.BaselineSource ?? "(未指定)")}`"));
        writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"- 当前: `{EscapeMd(diff.CurrentSource ?? "(未指定)")}`"));
        writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"- 显著漂移字段数: **{diff.SignificantDriftCount}**"));
        writer.WriteLine();

        if (diff.CoverageChanges.Count > 0)
        {
            writer.WriteLine("### 覆盖率变化");
            writer.WriteLine();
            writer.WriteLine("| 字段 | 基线 | 当前 | Δ |");
            writer.WriteLine("|------|------|------|---|");
            foreach (var c in diff.CoverageChanges)
            {
                writer.Write("| `"); writer.Write(EscapeMd(c.Path)); writer.Write("` | ");
                writer.Write(c.BaselineCoverage.ToString("P1", CultureInfo.InvariantCulture)); writer.Write(" | ");
                writer.Write(c.CurrentCoverage.ToString("P1", CultureInfo.InvariantCulture)); writer.Write(" | ");
                writer.Write((c.Delta >= 0 ? "+" : "") + c.Delta.ToString("P1", CultureInfo.InvariantCulture));
                writer.WriteLine(" |");
            }
            writer.WriteLine();
        }

        if (diff.NumericDrifts.Count > 0)
        {
            writer.WriteLine("### 数值分布漂移 (t-digest 质心对比)");
            writer.WriteLine();
            writer.WriteLine("| 字段 | 基线均值 | 当前均值 | 均值偏移 (σ) |");
            writer.WriteLine("|------|----------|----------|-------------|");
            foreach (var d in diff.NumericDrifts)
            {
                string shift = d.MeanShiftSigma.HasValue
                    ? $"{d.MeanShift?.ToString("F3", CultureInfo.InvariantCulture)} ({d.MeanShiftSigma?.ToString("F2", CultureInfo.InvariantCulture)}σ)"
                    : d.MeanShift?.ToString("F3", CultureInfo.InvariantCulture) ?? "—";
                writer.Write("| `"); writer.Write(EscapeMd(d.Path)); writer.Write("` | ");
                writer.Write(d.BaselineMean?.ToString("F3", CultureInfo.InvariantCulture) ?? "—"); writer.Write(" | ");
                writer.Write(d.CurrentMean?.ToString("F3", CultureInfo.InvariantCulture) ?? "—"); writer.Write(" | ");
                writer.Write(shift);
                writer.WriteLine(" |");
            }
            writer.WriteLine();
        }

        if (diff.DistinctDrifts.Count > 0)
        {
            writer.WriteLine("### 枚举分布漂移 (HyperLogLog Jaccard)");
            writer.WriteLine();
            writer.WriteLine("| 字段 | 基线唯一数 | 当前唯一数 | Jaccard | 严重度 |");
            writer.WriteLine("|------|------------|------------|---------|--------|");
            foreach (var d in diff.DistinctDrifts)
            {
                writer.Write("| `"); writer.Write(EscapeMd(d.Path)); writer.Write("` | ");
                writer.Write(d.BaselineDistinct?.ToString(CultureInfo.InvariantCulture) ?? "—"); writer.Write(" | ");
                writer.Write(d.CurrentDistinct?.ToString(CultureInfo.InvariantCulture) ?? "—"); writer.Write(" | ");
                writer.Write(d.Jaccard?.ToString("F2", CultureInfo.InvariantCulture) ?? "—"); writer.Write(" | ");
                writer.Write(d.Severity);
                writer.WriteLine(" |");
            }
            writer.WriteLine();
        }
    }
}
