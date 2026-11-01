using System.Globalization;

namespace ObjectAnalyzer.Reporting;

public static partial class HtmlReportExporter
{
    private static void WriteSamplingConfidenceSection(TextWriter writer, AnalysisResult result)
    {
        var sc = result.SamplingConfidence;
        if (sc == null) return;

        writer.WriteLine("<div class=\"section\">");
        writer.WriteLine("<h2>采样不确定性 (Wilson 95% 置信区间)</h2>");
        writer.WriteLine("<p>" + Escape(sc.Summary) + "</p>");
        writer.WriteLine("<table><thead><tr><th>字段</th><th>观测覆盖</th><th>区间下界</th><th>区间上界</th><th>区间宽度</th><th>状态</th></tr></thead><tbody>");
        foreach (var (field, ci) in sc.CoverageIntervals.OrderByDescending(p => p.Value.Width))
        {
            bool isLow = sc.LowConfidenceFields.Contains(field);
            string status = ci.Ambiguous
                ? "<span class=\"violation\">含义模糊 (上界 &lt; 5%)</span>"
                : isLow
                    ? "<span class=\"violation\">低可信 (宽度 ≥ 0.2)</span>"
                    : "<span class=\"quality-pass\">可信</span>";
            writer.Write("<tr><td><code>"); writer.Write(Escape(field)); writer.Write("</code></td>");
            writer.Write("<td>"); writer.Write(ci.Observed.ToString("P1", CultureInfo.InvariantCulture)); writer.Write("</td>");
            writer.Write("<td>"); writer.Write(ci.Lower.ToString("P1", CultureInfo.InvariantCulture)); writer.Write("</td>");
            writer.Write("<td>"); writer.Write(ci.Upper.ToString("P1", CultureInfo.InvariantCulture)); writer.Write("</td>");
            writer.Write("<td>"); writer.Write(ci.Width.ToString("P3", CultureInfo.InvariantCulture)); writer.Write("</td>");
            writer.Write("<td>"); writer.Write(status); writer.WriteLine("</td></tr>");
        }
        writer.WriteLine("</tbody></table>");
        if (sc.LowConfidenceFields.Count > 0)
        {
            writer.Write("<p class=\"quality-fail\">⚠️ 低可信字段: ");
            writer.Write(string.Join(", ", sc.LowConfidenceFields.Select(f => $"<code>{Escape(f)}</code>")));
            writer.WriteLine("</p>");
        }
        writer.WriteLine("</div>");
    }

    private static void WriteCorrelationSection(TextWriter writer, AnalysisResult result)
    {
        var cm = result.CorrelationMatrix;
        if (cm == null || cm.Fields.Count == 0) return;

        writer.WriteLine("<div class=\"section\">");
        writer.WriteLine("<h2>字段相关性矩阵</h2>");
        writer.Write("<p>样本数: ");
        writer.Write(cm.Samples.ToString(CultureInfo.InvariantCulture));
        writer.Write("；参与字段: ");
        writer.Write(cm.Fields.Count.ToString(CultureInfo.InvariantCulture));
        writer.WriteLine("</p>");

        // 强相关对快速浏览
        if (cm.StrongPairs.Count > 0)
        {
            writer.WriteLine("<h3>强相关对 (|Pearson| ≥ 0.7，样本 ≥ 30)</h3>");
            writer.WriteLine("<table><thead><tr><th>字段 A</th><th>字段 B</th><th>Pearson</th><th>Spearman</th><th>样本数</th></tr></thead><tbody>");
            foreach (var p in cm.StrongPairs)
            {
                writer.Write("<tr><td><code>"); writer.Write(Escape(p.FieldA)); writer.Write("</code></td>");
                writer.Write("<td><code>"); writer.Write(Escape(p.FieldB)); writer.Write("</code></td>");
                writer.Write("<td>"); writer.Write(p.Pearson?.ToString("F3", CultureInfo.InvariantCulture) ?? "—"); writer.Write("</td>");
                writer.Write("<td>"); writer.Write(p.Spearman?.ToString("F3", CultureInfo.InvariantCulture) ?? "—"); writer.Write("</td>");
                writer.Write("<td>"); writer.Write(p.Samples.ToString(CultureInfo.InvariantCulture)); writer.WriteLine("</td></tr>");
            }
            writer.WriteLine("</tbody></table>");
        }

        // 完整 Pearson 矩阵
        WriteMatrixTable(writer, "Pearson 相关系数", cm.Fields, cm.Pearson, cm.PairwiseSampleCounts);
        WriteMatrixTable(writer, "Spearman 秩相关系数", cm.Fields, cm.Spearman, cm.PairwiseSampleCounts);

        writer.WriteLine("</div>");
    }

    private static void WriteMatrixTable(TextWriter writer, string title, List<string> fields, List<List<double?>> matrix, List<List<long>> samples)
    {
        writer.WriteLine("<h3>" + Escape(title) + "</h3>");
        writer.WriteLine("<div class=\"matrix-wrap\"><table class=\"matrix\"><thead><tr><th></th>");
        foreach (var f in fields) writer.Write("<th><code>" + Escape(f) + "</code></th>");
        writer.WriteLine("</tr></thead><tbody>");
        for (int i = 0; i < fields.Count; i++)
        {
            writer.Write("<tr><th><code>"); writer.Write(Escape(fields[i])); writer.Write("</code></th>");
            for (int j = 0; j < fields.Count; j++)
            {
                var v = i < matrix.Count && j < matrix[i].Count ? matrix[i][j] : null;
                string cell;
                if (v == null) cell = "<span class=\"na\">—</span>";
                else
                {
                    double d = v.Value;
                    string color = Math.Abs(d) >= 0.7 ? (d > 0 ? "#d32f2f" : "#1565c0") : "#666";
                    cell = $"<span style=\"color:{color};font-weight:600\">{d.ToString("F2", CultureInfo.InvariantCulture)}</span>";
                }
                writer.Write("<td>"); writer.Write(cell); writer.Write("</td>");
            }
            writer.WriteLine("</tr>");
        }
        writer.WriteLine("</tbody></table></div>");
    }

    private static void WriteFuzzyDedupSection(TextWriter writer, AnalysisResult result)
    {
        var fd = result.FuzzyDuplicates;
        if (fd == null) return;

        writer.WriteLine("<div class=\"section\">");
        writer.WriteLine("<h2>模糊去重 (相似重复检测)</h2>");
        writer.Write("<p>总样本: ");
        writer.Write(fd.TotalSamples.ToString(CultureInfo.InvariantCulture));
        writer.Write("，相似重复组: <strong>");
        writer.Write(fd.GroupCount.ToString(CultureInfo.InvariantCulture));
        writer.Write("</strong>，相似重复样本数: ");
        writer.Write(fd.SimilarDuplicates.ToString(CultureInfo.InvariantCulture));
        double ratio = fd.TotalSamples > 0 ? (double)fd.SimilarDuplicates / fd.TotalSamples : 0;
        writer.Write(" (");
        writer.Write(ratio.ToString("P1", CultureInfo.InvariantCulture));
        writer.WriteLine(")</p>");

        if (fd.Groups.Count > 0)
        {
            writer.WriteLine("<table><thead><tr><th>代表样本</th><th>规范化键</th><th>样本数</th><th>平均相似度</th><th>样例</th></tr></thead><tbody>");
            foreach (var g in fd.Groups.Take(20))
            {
                string examples = string.Join(", ", g.Examples.Take(3).Select(e => $"`{Escape(Truncate(e, 30))}`"));
                writer.Write("<tr><td><code>"); writer.Write(Escape(Truncate(g.RepresentativeJson, 60))); writer.Write("</code></td>");
                writer.Write("<td><code>"); writer.Write(Escape(g.CanonicalKey)); writer.Write("</code></td>");
                writer.Write("<td>"); writer.Write(g.Count.ToString(CultureInfo.InvariantCulture)); writer.Write("</td>");
                writer.Write("<td>"); writer.Write(g.AverageSimilarity.ToString("F2", CultureInfo.InvariantCulture)); writer.Write("</td>");
                writer.Write("<td>"); writer.Write(examples); writer.WriteLine("</td></tr>");
            }
            writer.WriteLine("</tbody></table>");
            if (fd.Groups.Count > 20)
            {
                writer.Write("<p class=\"na\">… 仅展示前 20 组（共 ");
                writer.Write(fd.Groups.Count.ToString(CultureInfo.InvariantCulture));
                writer.WriteLine(" 组）。</p>");
            }
        }
        writer.WriteLine("</div>");
    }

    private static void WriteStringPatternsSection(TextWriter writer, AnalysisResult result)
    {
        if (result.StringPatterns == null || result.StringPatterns.Count == 0) return;

        writer.WriteLine("<div class=\"section\">");
        writer.WriteLine("<h2>字符串模式归纳</h2>");
        writer.WriteLine("<table><thead><tr><th>字段</th><th>主导模板</th><th>样本数</th><th>模板 Top 5 (模板 × 计数, 占比)</th></tr></thead><tbody>");
        foreach (var (field, profile) in result.StringPatterns.OrderByDescending(p => p.Value.TotalSamples).Take(20))
        {
            string dominant = profile.DominantPattern != null
                ? $"`{Escape(profile.DominantPattern.Template)}` ×{profile.DominantPattern.Count}"
                : "—";
            string topPatterns = string.Join("<br>", profile.Patterns.Take(5).Select(t =>
            {
                string pct = t.Ratio.HasValue ? t.Ratio.Value.ToString("P0", CultureInfo.InvariantCulture) : "—";
                return $"`{Escape(t.Template)}` ×{t.Count} ({pct})";
            }));
            writer.Write("<tr><td><code>"); writer.Write(Escape(field)); writer.Write("</code></td>");
            writer.Write("<td>"); writer.Write(dominant); writer.Write("</td>");
            writer.Write("<td>"); writer.Write(profile.TotalSamples.ToString(CultureInfo.InvariantCulture)); writer.Write("</td>");
            writer.Write("<td>"); writer.Write(topPatterns); writer.WriteLine("</td></tr>");
        }
        writer.WriteLine("</tbody></table>");
        writer.WriteLine("</div>");
    }

    private static void WriteUnicodeHygieneSection(TextWriter writer, AnalysisResult result)
    {
        if (result.UnicodeHygiene == null || result.UnicodeHygiene.Count == 0) return;

        writer.WriteLine("<div class=\"section\">");
        writer.WriteLine("<h2>Unicode 卫生检查 (中文场景脏数据)</h2>");
        writer.WriteLine("<table><thead><tr><th>字段</th><th>样本</th><th>全/半角混用</th><th>不可见字符</th><th>控制字符</th><th>零宽字符</th><th>前后空白</th><th>大小写混用</th><th>卫生评分</th></tr></thead><tbody>");
        foreach (var (field, r) in result.UnicodeHygiene.OrderBy(p => p.Value.HygieneScore ?? 100.0).Take(15))
        {
            string mixedFullHalf = r.FullwidthCount > 0 && r.HalfwidthCount > 0
                ? $"<span class=\"violation\">{r.FullwidthCount}全 / {r.HalfwidthCount}半</span>" : "—";
            string invisible = r.InvisibleCount > 0 ? $"<span class=\"violation\">{r.InvisibleCount}</span>" : "—";
            string control = r.ControlCharCount > 0 ? $"<span class=\"violation\">{r.ControlCharCount}</span>" : "—";
            string zeroWidth = r.ZeroWidthCount > 0 ? $"<span class=\"violation\">{r.ZeroWidthCount}</span>" : "—";
            string surroundWs = r.SurroundingWhitespaceCount > 0 ? $"<span class=\"violation\">{r.SurroundingWhitespaceCount}</span>" : "—";
            string mixedCase = r.MixedCaseCount > 0 ? $"<span class=\"violation\">{r.MixedCaseCount} ({r.MixedCaseRatio?.ToString("P0", CultureInfo.InvariantCulture)})</span>" : "—";
            string score = r.HygieneScore.HasValue
                ? (r.HygieneScore.Value >= 90 ? $"<span class=\"quality-pass\">{r.HygieneScore:F0}</span>"
                : r.HygieneScore.Value >= 70 ? $"<span style=\"color:#e65100\">{r.HygieneScore:F0}</span>"
                : $"<span class=\"quality-fail\">{r.HygieneScore:F0}</span>")
                : "—";
            writer.Write("<tr><td><code>"); writer.Write(Escape(field)); writer.Write("</code></td>");
            writer.Write("<td>"); writer.Write(r.TotalSamples.ToString(CultureInfo.InvariantCulture)); writer.Write("</td>");
            writer.Write("<td>"); writer.Write(mixedFullHalf); writer.Write("</td>");
            writer.Write("<td>"); writer.Write(invisible); writer.Write("</td>");
            writer.Write("<td>"); writer.Write(control); writer.Write("</td>");
            writer.Write("<td>"); writer.Write(zeroWidth); writer.Write("</td>");
            writer.Write("<td>"); writer.Write(surroundWs); writer.Write("</td>");
            writer.Write("<td>"); writer.Write(mixedCase); writer.Write("</td>");
            writer.Write("<td>"); writer.Write(score); writer.WriteLine("</td></tr>");
        }
        writer.WriteLine("</tbody></table>");
        writer.WriteLine("<p class=\"na\">仅展示卫生评分最低的 15 个字段。卫生评分 0-100：综合全角混用、不可见字符、控制字符、大小写混用，越高越干净。</p>");
        writer.WriteLine("</div>");
    }

    private static void WriteTimeSeriesSection(TextWriter writer, AnalysisResult result)
    {
        if (result.TimeSeriesAnalyses == null || result.TimeSeriesAnalyses.Count == 0) return;

        writer.WriteLine("<div class=\"section\">");
        writer.WriteLine("<h2>时间序列深度分析</h2>");
        writer.WriteLine("<table><thead><tr><th>字段</th><th>时间窗</th><th>桶跨度</th><th>桶数</th><th>趋势</th><th>斜率</th><th>周期 (桶)</th><th>空桶占比</th><th>脉冲性</th></tr></thead><tbody>");
        foreach (var (field, ts) in result.TimeSeriesAnalyses)
        {
            string trendColor = ts.Trend == "rising" ? "#2e7d32" : ts.Trend == "falling" ? "#c62828" : ts.Trend == "stable" ? "#666" : "#999";
            string period = ts.DetectedPeriod.HasValue ? $"{ts.DetectedPeriod} (强度 {ts.PeriodicityStrength?.ToString("F2", CultureInfo.InvariantCulture)})" : "—";
            string emptyRatio = ts.EmptyRatio > 0.5
                ? $"<span class=\"violation\">{ts.EmptyRatio:P0}</span>"
                : ts.EmptyRatio.ToString("P0", CultureInfo.InvariantCulture);
            string burst = ts.Burstiness >= 3
                ? $"<span class=\"violation\">{ts.Burstiness:F2}</span>"
                : ts.Burstiness.ToString("F2", CultureInfo.InvariantCulture);
            writer.Write("<tr><td><code>"); writer.Write(Escape(field)); writer.Write("</code></td>");
            writer.Write("<td>"); writer.Write($"{ts.Start:yyyy-MM-dd} ~ {ts.End:yyyy-MM-dd}"); writer.Write("</td>");
            writer.Write("<td>"); writer.Write(ts.BucketSpan.ToString()); writer.Write("</td>");
            writer.Write("<td>"); writer.Write(ts.BucketCount.ToString(CultureInfo.InvariantCulture)); writer.Write("</td>");
            writer.Write("<td><span style=\"color:"); writer.Write(trendColor); writer.Write(";font-weight:600\">"); writer.Write(Escape(ts.Trend)); writer.Write("</span></td>");
            writer.Write("<td>"); writer.Write(ts.TrendSlope?.ToString("F3", CultureInfo.InvariantCulture) ?? "—"); writer.Write("</td>");
            writer.Write("<td>"); writer.Write(period); writer.Write("</td>");
            writer.Write("<td>"); writer.Write(emptyRatio); writer.Write("</td>");
            writer.Write("<td>"); writer.Write(burst); writer.WriteLine("</td></tr>");
        }
        writer.WriteLine("</tbody></table>");
        writer.WriteLine("<p class=\"na\">趋势判定：R²≥0.4 且斜率显著。周期：自相关峰值 ≥ 0.4。脉冲性：峰值/中位数 ≥ 3 视为脉冲型。</p>");
        writer.WriteLine("</div>");
    }

    private static void WriteDistributionDiffSection(TextWriter writer, AnalysisResult result)
    {
        var diff = result.DistributionDiff;
        if (diff == null) return;

        writer.WriteLine("<div class=\"section\">");
        writer.WriteLine("<h2>值分布漂移 (vs 草图基线)</h2>");
        writer.Write("<p>基线: <code>");
        writer.Write(Escape(diff.BaselineSource ?? "(未指定)"));
        writer.Write("</code>，当前: <code>");
        writer.Write(Escape(diff.CurrentSource ?? "(未指定)"));
        writer.Write("</code>，显著漂移字段数: <strong>");
        writer.Write(diff.SignificantDriftCount.ToString(CultureInfo.InvariantCulture));
        writer.WriteLine("</strong></p>");

        if (diff.CoverageChanges.Count > 0)
        {
            writer.WriteLine("<h3>覆盖率变化</h3>");
            writer.WriteLine("<table><thead><tr><th>字段</th><th>基线</th><th>当前</th><th>Δ</th></tr></thead><tbody>");
            foreach (var c in diff.CoverageChanges)
            {
                string color = c.Delta > 0 ? "#2e7d32" : c.Delta < 0 ? "#c62828" : "#666";
                writer.Write("<tr><td><code>"); writer.Write(Escape(c.Path)); writer.Write("</code></td>");
                writer.Write("<td>"); writer.Write(c.BaselineCoverage.ToString("P1", CultureInfo.InvariantCulture)); writer.Write("</td>");
                writer.Write("<td>"); writer.Write(c.CurrentCoverage.ToString("P1", CultureInfo.InvariantCulture)); writer.Write("</td>");
                writer.Write("<td><span style=\"color:"); writer.Write(color); writer.Write("\">");
                writer.Write((c.Delta >= 0 ? "+" : "") + c.Delta.ToString("P1", CultureInfo.InvariantCulture));
                writer.WriteLine("</span></td></tr>");
            }
            writer.WriteLine("</tbody></table>");
        }

        if (diff.NumericDrifts.Count > 0)
        {
            writer.WriteLine("<h3>数值分布漂移 (t-digest 质心对比)</h3>");
            writer.WriteLine("<table><thead><tr><th>字段</th><th>基线均值</th><th>当前均值</th><th>均值偏移 (σ)</th></tr></thead><tbody>");
            foreach (var d in diff.NumericDrifts)
            {
                string shift = d.MeanShiftSigma.HasValue
                    ? $"{d.MeanShift?.ToString("F3", CultureInfo.InvariantCulture)} ({d.MeanShiftSigma?.ToString("F2", CultureInfo.InvariantCulture)}σ)"
                    : d.MeanShift?.ToString("F3", CultureInfo.InvariantCulture) ?? "—";
                string shiftColor = d.MeanShiftSigma >= 2 ? "#c62828" : d.MeanShiftSigma >= 1 ? "#e65100" : "#666";
                writer.Write("<tr><td><code>"); writer.Write(Escape(d.Path)); writer.Write("</code></td>");
                writer.Write("<td>"); writer.Write(d.BaselineMean?.ToString("F3", CultureInfo.InvariantCulture) ?? "—"); writer.Write("</td>");
                writer.Write("<td>"); writer.Write(d.CurrentMean?.ToString("F3", CultureInfo.InvariantCulture) ?? "—"); writer.Write("</td>");
                writer.Write("<td><span style=\"color:"); writer.Write(shiftColor); writer.Write("\">");
                writer.Write(shift); writer.WriteLine("</span></td></tr>");
            }
            writer.WriteLine("</tbody></table>");
        }

        if (diff.DistinctDrifts.Count > 0)
        {
            writer.WriteLine("<h3>枚举分布漂移 (HyperLogLog Jaccard)</h3>");
            writer.WriteLine("<table><thead><tr><th>字段</th><th>基线唯一数</th><th>当前唯一数</th><th>Jaccard</th><th>严重度</th></tr></thead><tbody>");
            foreach (var d in diff.DistinctDrifts)
            {
                string sevColor = d.Severity == "high" ? "#c62828" : d.Severity == "medium" ? "#e65100" : "#666";
                writer.Write("<tr><td><code>"); writer.Write(Escape(d.Path)); writer.Write("</code></td>");
                writer.Write("<td>"); writer.Write(d.BaselineDistinct?.ToString(CultureInfo.InvariantCulture) ?? "—"); writer.Write("</td>");
                writer.Write("<td>"); writer.Write(d.CurrentDistinct?.ToString(CultureInfo.InvariantCulture) ?? "—"); writer.Write("</td>");
                writer.Write("<td>"); writer.Write(d.Jaccard?.ToString("F2", CultureInfo.InvariantCulture) ?? "—"); writer.Write("</td>");
                writer.Write("<td><span style=\"color:"); writer.Write(sevColor); writer.Write(";font-weight:600\">");
                writer.Write(Escape(d.Severity)); writer.WriteLine("</span></td></tr>");
            }
            writer.WriteLine("</tbody></table>");
        }

        if (diff.AddedFields.Count > 0)
        {
            writer.WriteLine("<h3>新增字段 (基线中不存在)</h3><p>");
            writer.Write(string.Join(", ", diff.AddedFields.Select(f => $"<code>{Escape(f.Path)}</code>")));
            writer.WriteLine("</p>");
        }
        if (diff.RemovedFields.Count > 0)
        {
            writer.WriteLine("<h3>删除字段</h3><p>");
            writer.Write(string.Join(", ", diff.RemovedFields.Select(f => $"<code>{Escape(f.Path)}</code>")));
            writer.WriteLine("</p>");
        }
        writer.WriteLine("</div>");
    }
}
