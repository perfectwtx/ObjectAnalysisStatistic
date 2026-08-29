using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Spectre.Console;

namespace ObjectAnalyzer.Reporting;

public static class HtmlReportExporter
{
    public static void Save(AnalysisResult result, string outputPath)
    {
        AnsiConsole.MarkupLine($"正在将 HTML 报告保存至: [blue]{Markup.Escape(outputPath)}[/] ...");
        // 逐段写入，避免大报告整体常驻内存
        using (var writer = new StreamWriter(outputPath, false, Encoding.UTF8))
        {
            WriteHtml(writer, result);
        }
        AnsiConsole.MarkupLine($"[green]HTML 报告成功保存！[/]");
    }

    private static void WriteHtml(TextWriter writer, AnalysisResult result)
    {
        const string title = "ObjectAnalyzer 分析报告";
        string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        // 准备图表数据
        var fieldNames = result.FieldStatistics.Select(f => EscapeJs(f.FieldName)).ToArray();
        var coverages = result.FieldStatistics.Select(f => f.Coverage.ToString("F1", CultureInfo.InvariantCulture)).ToArray();

        var topFields = result.FieldStatistics.Take(10).ToList();

        writer.WriteLine("<!DOCTYPE html>");
        writer.WriteLine("<html lang=\"zh-CN\">");
        writer.WriteLine("<head>");
        writer.WriteLine("<meta charset=\"UTF-8\">");
        writer.WriteLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
        writer.Write("<title>"); writer.Write(title); writer.WriteLine("</title>");
        writer.WriteLine("<script src=\"https://cdn.jsdelivr.net/npm/chart.js@4\"></script>");
        writer.WriteLine("<style>");
        writer.WriteLine(CssStyles);
        writer.WriteLine("</style>");
        writer.WriteLine("</head>");
        writer.WriteLine("<body>");

        // 头部
        writer.WriteLine("<div class=\"header\">");
        writer.Write("<h1>"); writer.Write(title); writer.WriteLine("</h1>");
        writer.Write("<p class=\"timestamp\">生成时间: "); writer.Write(timestamp); writer.WriteLine("</p>");
        if (!string.IsNullOrEmpty(result.FilterExpression))
        {
            writer.Write("<p class=\"filter-line\">过滤条件: <code>"); writer.Write(Escape(result.FilterExpression)); writer.WriteLine("</code></p>");
        }
        writer.WriteLine("</div>");

        // 摘要卡片
        writer.WriteLine("<div class=\"cards\">");
        writer.Write(Card("总对象数", result.TotalObjects.ToString(CultureInfo.InvariantCulture), "blue"));
        writer.Write(Card("唯一对象数", result.TotalUniqueObjects.ToString(CultureInfo.InvariantCulture), "green"));
        writer.Write(Card("重复组数", result.TotalDuplicateGroups.ToString(CultureInfo.InvariantCulture),
            result.TotalDuplicateGroups > 0 ? "orange" : "green"));
        writer.Write(Card("字段数", result.FieldStatistics.Count.ToString(CultureInfo.InvariantCulture), "purple"));
        writer.WriteLine("</div>");

        // 数据质量摘要
        var quality = DataQualityReport.Build(result);
        writer.WriteLine("<div class=\"section\">");
        writer.WriteLine("<h2>数据质量</h2>");
        if (quality.Passed)
        {
            writer.Write("<p class=\"quality-pass\">✅ 通过 — ");
            writer.Write(quality.TotalFields.ToString(CultureInfo.InvariantCulture));
            writer.WriteLine(" 个字段，无枚举违规。</p>");
        }
        else
        {
            writer.Write("<p class=\"quality-fail\">❌ 未通过 — ");
            writer.Write(quality.FieldsWithViolations.ToString(CultureInfo.InvariantCulture));
            writer.Write('/');
            writer.Write(quality.TotalFields.ToString(CultureInfo.InvariantCulture));
            writer.Write(" 个字段存在 ");
            writer.Write(quality.TotalViolationCount.ToString(CultureInfo.InvariantCulture));
            writer.Write(" 条枚举违规（违规率 ");
            writer.Write(quality.ViolationRate.ToString("P1", CultureInfo.InvariantCulture));
            writer.WriteLine("）。</p>");
        }
        writer.WriteLine("</div>");

        // 图表区域
        writer.WriteLine("<div class=\"charts\">");
        writer.WriteLine("<div class=\"chart-container\">");
        writer.WriteLine("<h2>字段覆盖率</h2>");
        writer.WriteLine("<canvas id=\"coverageChart\"></canvas>");
        writer.WriteLine("</div>");

        if (topFields.Count > 0)
        {
            writer.WriteLine("<div class=\"chart-container\">");
            writer.WriteLine("<h2>字段类型分布 (Top 10)</h2>");
            writer.WriteLine("<canvas id=\"typeChart\"></canvas>");
            writer.WriteLine("</div>");
        }
        writer.WriteLine("</div>");

        // 重复对象
        if (result.TotalDuplicateGroups > 0)
        {
            writer.WriteLine("<div class=\"section\">");
            writer.Write("<h2>重复对象 ("); writer.Write(result.TotalDuplicateGroups.ToString(CultureInfo.InvariantCulture)); writer.WriteLine(" 组)</h2>");
            writer.WriteLine("<table><thead><tr><th>重复次数</th><th>对象内容</th></tr></thead><tbody>");
            foreach (var dup in result.DuplicateObjects)
            {
                writer.Write("<tr><td>"); writer.Write(dup.Count.ToString(CultureInfo.InvariantCulture));
                writer.Write("</td><td><code>"); writer.Write(Escape(dup.ObjectJson)); writer.WriteLine("</code></td></tr>");
            }
            writer.WriteLine("</tbody></table>");
            if (result.TotalDuplicateGroups > result.DuplicateObjects.Count)
            {
                writer.Write("<p class=\"na\">… 完整 JSON 已省略，仅保留 ");
                writer.Write(result.DuplicateObjects.Count.ToString(CultureInfo.InvariantCulture));
                writer.Write(" 组示例（共 "); writer.Write(result.TotalDuplicateGroups.ToString(CultureInfo.InvariantCulture)); writer.WriteLine(" 组）。</p>");
            }
            writer.WriteLine("</div>");
        }

        // 字段详情表
        writer.WriteLine("<div class=\"section\">");
        writer.WriteLine("<h2>字段详情</h2>");
        writer.WriteLine("<table><thead><tr><th>字段名</th><th>语义推断</th><th>覆盖率</th><th>极值 / 长度</th><th>主导类型</th><th>默认值</th><th>规则</th><th>详情</th></tr></thead><tbody>");
        foreach (var stat in result.FieldStatistics)
        {
            string defaultVal = stat.DefaultValueRepresentation != null
                ? $"<code>{Escape(stat.DefaultValueRepresentation)}</code>"
                : "<span class=\"na\">—</span>";
            string semanticVal = !string.IsNullOrEmpty(stat.SemanticType)
                ? $"<span class=\"semantic-badge\">{Escape(stat.SemanticType)}</span>"
                : "<span class=\"na\">—</span>";

            string metricInfo = "";
            if (stat.MinNumber.HasValue && stat.MaxNumber.HasValue)
            {
                metricInfo += $"<div>数值: {stat.MinNumber:G4} ~ {stat.MaxNumber:G4}</div>";
                if (stat.AvgNumber.HasValue)
                    metricInfo += $"<div>均值: {stat.AvgNumber:G4}</div>";
                if (stat.StdDevNumber.HasValue)
                    metricInfo += $"<div>标准差: {stat.StdDevNumber:G4}</div>";
                if (stat.SumNumber.HasValue)
                    metricInfo += $"<div>总和: {stat.SumNumber:G4}</div>";
                if (stat.FormatQuantiles() is { } quantiles)
                    metricInfo += $"<div>{Escape(quantiles)}</div>";
                if (stat.Histogram is { Count: > 0 })
                    metricInfo += $"<div>直方图: {Escape(string.Join("  ", stat.Histogram.Select(b => $"{b}×{b.Count}")))}</div>";
                if (stat.OutlierCount.HasValue && stat.OutlierRatio.HasValue)
                {
                    string ratioColor = stat.OutlierRatio.Value > 0.1 ? "#e65100" : "#666";
                    metricInfo += $"<div>异常值: <span style=\"color:{ratioColor}\">{stat.OutlierCount.Value.ToString(CultureInfo.InvariantCulture)} ({stat.OutlierRatio.Value.ToString("P1", CultureInfo.InvariantCulture)})</span>，区间 <code>{Escape(stat.OutlierBounds ?? "-")}</code> ({Escape(stat.OutlierMethod ?? "")} 近似)</div>";
                }
            }
            if (stat.MinLength.HasValue && stat.MaxLength.HasValue)
            {
                string avgLen = stat.AvgLength.HasValue ? $"，平均 {stat.AvgLength:G3}" : "";
                metricInfo += $"<div>长度: {stat.MinLength} ~ {stat.MaxLength} 字符{avgLen}</div>";
            }
            if (stat.MinDateTime.HasValue && stat.MaxDateTime.HasValue)
            {
                metricInfo += $"<div>时间范围: {stat.MinDateTime:yyyy-MM-dd HH:mm:ss} ~ {stat.MaxDateTime:yyyy-MM-dd HH:mm:ss} UTC (样本 {stat.DateTimeCount?.ToString(CultureInfo.InvariantCulture)})</div>";
                if (stat.DateHistogram is { Count: > 0 })
                {
                    metricInfo += "<div>时间直方图: " + Escape(string.Join("  ", stat.DateHistogram.Select(b => $"{b}×{b.Count}"))) + "</div>";
                }
            }
            if (stat.DistinctCount.HasValue)
            {
                string approxMark = stat.DistinctApproximate ? "≈" : "";
                metricInfo += $"<div>唯一值: {approxMark}{stat.DistinctCount.Value.ToString(CultureInfo.InvariantCulture)}</div>";
            }
            if (string.IsNullOrEmpty(metricInfo)) metricInfo = "<span class=\"na\">—</span>";

            string types = string.Join(", ", stat.TypeCounts.Select(t => $"{t.Key}({t.Value})"));
            string topValues = string.Join(", ", stat.ValueCounts
                .OrderByDescending(v => v.Value).Take(3)
                .Select(v => $"{Escape(Truncate(v.Key, 30))} ×{v.Value.ToString(CultureInfo.InvariantCulture)}"));

            string coveragePct = (stat.Coverage * 100).ToString("F1", CultureInfo.InvariantCulture);
            string ruleInfo = stat.AppliedRule != null ? "<span class=\"rule-badge\">已应用</span>" : "";
            if (stat.TransformType != null)
            {
                ruleInfo += $"<span class=\"transform-badge\">转换: {Escape(stat.TransformType)}";
                if (stat.TransformFailureCount > 0)
                {
                    ruleInfo += $" (失败 {stat.TransformFailureCount})";
                }
                ruleInfo += "</span>";
            }
            if (stat.EnumViolations.Count > 0)
            {
                var violations = string.Join(", ", stat.EnumViolations.Select(v => $"<span class=\"violation\">{Escape(v)}</span>"));
                ruleInfo += $"<div class=\"violations\">枚举违规: {violations}</div>";
            }
            foreach (var check in result.QualityViolations)
            {
                if (check.Check == QualityCheckTypes.EnumValues ||
                    !string.Equals(check.Field, stat.FieldName, StringComparison.Ordinal))
                    continue;
                string approxMark = check.Approximate ? "（近似）" : "";
                string samples = check.Samples.Count > 0
                    ? " — " + string.Join(", ", check.Samples.Select(s => $"<span class=\"violation\">{Escape(Truncate(s, 40))}</span>"))
                    : "";
                ruleInfo += $"<div class=\"violations\">{Escape(QualityCheckTypes.Label(check.Check))}{approxMark}: {Escape(check.Message)}{samples}</div>";
            }
            if (stat.NullAppeared)
                ruleInfo += "<span class=\"null-badge\">含 null</span>";
            if (stat.ValueCountsTruncated)
                ruleInfo += "<span class=\"trunc-badge\">值已截断</span>";

            writer.Write("<tr>");
            writer.Write("<td><code>"); writer.Write(Escape(stat.FieldName)); writer.Write("</code></td>");
            writer.Write("<td>"); writer.Write(semanticVal); writer.Write("</td>");
            writer.Write("<td>"); writer.Write(stat.Count.ToString(CultureInfo.InvariantCulture)); writer.Write('/'); writer.Write(result.TotalObjects.ToString(CultureInfo.InvariantCulture));
            writer.Write(" ("); writer.Write(coveragePct); writer.Write("%)</td>");
            writer.Write("<td>"); writer.Write(metricInfo); writer.Write("</td>");
            writer.Write("<td>"); writer.Write(Escape(types)); writer.Write("</td>");
            writer.Write("<td>"); writer.Write(defaultVal); writer.Write("</td>");
            writer.Write("<td>"); writer.Write(ruleInfo); writer.Write("</td>");
            writer.Write("<td class=\"detail\">"); writer.Write(topValues); writer.WriteLine("</td>");
            writer.WriteLine("</tr>");
        }
        writer.WriteLine("</tbody></table>");
        writer.WriteLine("</div>");

        // Schema 演进小节
        WriteSchemaDiffSection(writer, result);

        // P4 增强小节
        WriteSamplingConfidenceSection(writer, result);
        WriteCorrelationSection(writer, result);
        WriteFuzzyDedupSection(writer, result);
        WriteStringPatternsSection(writer, result);
        WriteUnicodeHygieneSection(writer, result);
        WriteTimeSeriesSection(writer, result);
        WriteDistributionDiffSection(writer, result);

        // Chart.js 脚本
        writer.WriteLine("<script>");

        writer.WriteLine("new Chart(document.getElementById('coverageChart'), {");
        writer.WriteLine("  type: 'bar',");
        writer.Write("  data: { labels: ["); writer.Write(string.Join(",", fieldNames.Select(n => $"'{n}'"))); writer.WriteLine("],");
        writer.Write("    datasets: [{ label: '覆盖率 (%)', data: ["); writer.Write(string.Join(",", coverages)); writer.WriteLine("],");
        writer.WriteLine("      backgroundColor: 'rgba(54,162,235,0.6)', borderColor: 'rgba(54,162,235,1)', borderWidth: 1 }]");
        writer.WriteLine("  },");
        writer.WriteLine("  options: { responsive: true, scales: { y: { beginAtZero: true, max: 100 } }, plugins: { legend: { display: false } } }");
        writer.WriteLine("});");

        if (topFields.Count > 0)
        {
            var allTypes = topFields.SelectMany(f => f.TypeCounts.Keys).Distinct().ToList();
            var colors = new[] { "rgba(54,162,235,0.7)", "rgba(255,99,132,0.7)", "rgba(75,192,192,0.7)",
                "rgba(255,205,86,0.7)", "rgba(153,102,255,0.7)", "rgba(255,159,64,0.7)" };

            writer.WriteLine("new Chart(document.getElementById('typeChart'), {");
            writer.WriteLine("  type: 'bar',");
            writer.Write("  data: { labels: ["); writer.Write(string.Join(",", topFields.Select(f => $"'{EscapeJs(f.FieldName)}'"))); writer.WriteLine("],");
            writer.Write("    datasets: [");
            for (int i = 0; i < allTypes.Count; i++)
            {
                string t = allTypes[i];
                string color = colors[i % colors.Length];
                var data = topFields.Select(f => f.TypeCounts.GetValueOrDefault(t, 0).ToString(CultureInfo.InvariantCulture));
                writer.Write(string.Create(CultureInfo.InvariantCulture, $"{{ label: '{EscapeJs(t)}', data: [{string.Join(",", data)}], backgroundColor: '{color}' }},"));
            }
            writer.WriteLine("] },");
            writer.WriteLine("  options: { responsive: true, scales: { x: { stacked: true }, y: { stacked: true } } }");
            writer.WriteLine("});");
        }

        writer.WriteLine("</script>");
        writer.WriteLine("</body></html>");
    }

    private static string Card(string label, string value, string color)
    {
        return $"<div class=\"card {color}\"><div class=\"card-value\">{value}</div><div class=\"card-label\">{label}</div></div>";
    }

    private static void WriteSchemaDiffSection(TextWriter writer, AnalysisResult result)
    {
        if (result.SchemaDiff == null) return;

        var diff = result.SchemaDiff;
        writer.WriteLine("<div class=\"section\">");
        writer.WriteLine("<h2>Schema 演进（vs 基线）</h2>");
        if (diff.HasBreakingChanges)
        {
            writer.WriteLine("<p class=\"quality-fail\">⚠️ <strong>含破坏性变更</strong>（删除字段 / 类型变化 / 可空性收紧 / coverage 大幅下降）</p>");
        }
        else
        {
            writer.WriteLine("<p class=\"quality-pass\">✅ 无破坏性变更</p>");
        }
        writer.Write("<p>基线: <code>");
        writer.Write(Escape(diff.BaselineSource ?? "(未指定)"));
        writer.Write("</code>，当前: <code>");
        writer.Write(Escape(diff.CurrentSource ?? "(未指定)"));
        writer.Write("</code></p>");
        writer.Write("<p>新增: ");
        writer.Write(diff.AddedFields.Count.ToString(CultureInfo.InvariantCulture));
        writer.Write("，删除: ");
        writer.Write(diff.RemovedFields.Count.ToString(CultureInfo.InvariantCulture));
        writer.Write("，变更: ");
        writer.Write(diff.ChangedFields.Count.ToString(CultureInfo.InvariantCulture));
        writer.WriteLine("</p>");

        if (diff.AddedFields.Count > 0)
        {
            writer.WriteLine("<h3>新增字段</h3>");
            writer.WriteLine("<table><thead><tr><th>字段</th><th>类型</th><th>coverage</th><th>语义</th></tr></thead><tbody>");
            foreach (var f in diff.AddedFields)
            {
                writer.Write("<tr><td><code>");
                writer.Write(Escape(f.Path));
                writer.Write("</code></td><td>");
                writer.Write(Escape(f.PrimaryType));
                writer.Write("</td><td>");
                writer.Write(f.Coverage.ToString("P1", CultureInfo.InvariantCulture));
                writer.Write("</td><td>");
                writer.Write(Escape(f.SemanticType ?? "—"));
                writer.WriteLine("</td></tr>");
            }
            writer.WriteLine("</tbody></table>");
        }

        if (diff.RemovedFields.Count > 0)
        {
            writer.WriteLine("<h3>删除字段（破坏性）</h3>");
            writer.WriteLine("<table><thead><tr><th>字段</th><th>类型</th><th>coverage</th><th>语义</th></tr></thead><tbody>");
            foreach (var f in diff.RemovedFields)
            {
                writer.Write("<tr><td><code>");
                writer.Write(Escape(f.Path));
                writer.Write("</code></td><td>");
                writer.Write(Escape(f.PrimaryType));
                writer.Write("</td><td>");
                writer.Write(f.Coverage.ToString("P1", CultureInfo.InvariantCulture));
                writer.Write("</td><td>");
                writer.Write(Escape(f.SemanticType ?? "—"));
                writer.WriteLine("</td></tr>");
            }
            writer.WriteLine("</tbody></table>");
        }

        if (diff.ChangedFields.Count > 0)
        {
            writer.WriteLine("<h3>变更字段</h3>");
            writer.WriteLine("<table><thead><tr><th>字段</th><th>破坏性</th><th>变化项</th></tr></thead><tbody>");
            foreach (var c in diff.ChangedFields)
            {
                writer.Write("<tr><td><code>");
                writer.Write(Escape(c.Path));
                writer.Write("</code></td><td>");
                writer.Write(c.Breaking ? "<strong style=\"color:#d32f2f\">是</strong>" : "否");
                writer.Write("</td><td><ul>");
                foreach (var item in c.Changes)
                {
                    string color = item.Breaking ? "#d32f2f" : "#666";
                    writer.Write("<li style=\"color:");
                    writer.Write(color);
                    writer.Write("\"><code>");
                    writer.Write(Escape(item.Property));
                    writer.Write("</code>: ");
                    writer.Write(Escape(item.Description));
                    writer.WriteLine("</li>");
                }
                writer.WriteLine("</ul></td></tr>");
            }
            writer.WriteLine("</tbody></table>");
        }

        writer.WriteLine("</div>");
    }

    // ─────────────────────────────────────────────────────
    // P4 增强小节
    // ─────────────────────────────────────────────────────

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

    private static string Escape(string s)
    {
        return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
            .Replace("\"", "&quot;").Replace("'", "&#39;");
    }

    private static string EscapeJs(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '\'': sb.Append("\\'"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '<': sb.Append("\\u003C"); break;
                case '>': sb.Append("\\u003E"); break;
                case '&': sb.Append("\\u0026"); break;
                case '/': sb.Append("\\/"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    private static string Truncate(string s, int maxLen)
    {
        return s.Length <= maxLen ? s : s[..(maxLen - 3)] + "...";
    }

    private const string CssStyles = """
        * { margin: 0; padding: 0; box-sizing: border-box; }
        body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; background: #f0f2f5; color: #333; padding: 24px; }
        .header { text-align: center; margin-bottom: 32px; }
        .header h1 { font-size: 1.8rem; color: #1a1a2e; }
        .timestamp { color: #888; font-size: 0.9rem; margin-top: 4px; }
        .filter-line { color: #7b1fa2; font-size: 0.9rem; margin-top: 6px; }
        .cards { display: flex; gap: 16px; justify-content: center; flex-wrap: wrap; margin-bottom: 32px; }
        .card { background: #fff; border-radius: 12px; padding: 20px 28px; min-width: 140px; text-align: center; box-shadow: 0 2px 8px rgba(0,0,0,0.08); border-top: 4px solid #ccc; }
        .card.blue { border-top-color: #36a2eb; } .card.green { border-top-color: #4caf50; }
        .card.orange { border-top-color: #ff9800; } .card.purple { border-top-color: #9c27b0; }
        .card-value { font-size: 2rem; font-weight: 700; }
        .card-label { font-size: 0.85rem; color: #888; margin-top: 4px; }
        .charts { display: flex; gap: 24px; flex-wrap: wrap; margin-bottom: 32px; }
        .chart-container { background: #fff; border-radius: 12px; padding: 20px; flex: 1; min-width: 360px; box-shadow: 0 2px 8px rgba(0,0,0,0.08); }
        .chart-container h2 { font-size: 1rem; color: #555; margin-bottom: 12px; }
        .section { background: #fff; border-radius: 12px; padding: 24px; margin-bottom: 24px; box-shadow: 0 2px 8px rgba(0,0,0,0.08); }
        .section h2 { font-size: 1.1rem; color: #1a1a2e; margin-bottom: 16px; border-bottom: 2px solid #f0f2f5; padding-bottom: 8px; }
        table { width: 100%; border-collapse: collapse; font-size: 0.9rem; }
        th { background: #f8f9fa; text-align: left; padding: 10px 12px; font-weight: 600; color: #555; border-bottom: 2px solid #e9ecef; }
        td { padding: 8px 12px; border-bottom: 1px solid #f0f2f5; vertical-align: top; }
        tr:hover { background: #f8f9fb; }
        code { background: #f0f2f5; padding: 2px 6px; border-radius: 4px; font-size: 0.85rem; word-break: break-all; }
        .detail { max-width: 300px; color: #666; font-size: 0.85rem; }
        .na { color: #ccc; }
        .semantic-badge { background: #f3e5f5; color: #7b1fa2; padding: 2px 8px; border-radius: 4px; font-size: 0.8rem; font-weight: 600; }
        .rule-badge { display: inline-block; background: #e7f1ff; color: #1b6ec2; padding: 2px 8px; border-radius: 4px; font-size: 0.8rem; }
        .transform-badge { display: inline-block; background: #f3e5f5; color: #7b1fa2; padding: 2px 8px; border-radius: 4px; font-size: 0.8rem; margin-left: 4px; }
        .violations { margin-top: 4px; font-size: 0.8rem; color: #842029; }
        .violation { background: #f8d7da; color: #842029; padding: 1px 4px; border-radius: 3px; margin: 0 2px; }
        .quality-pass { color: #15803d; font-weight: 600; }
        .quality-fail { color: #b91c1c; font-weight: 600; }
        .null-badge { display: inline-block; background: #fff3cd; color: #856404; padding: 2px 8px; border-radius: 4px; font-size: 0.8rem; margin-left: 4px; }
        .trunc-badge { display: inline-block; background: #ffe0b2; color: #e65100; padding: 2px 8px; border-radius: 4px; font-size: 0.8rem; margin-left: 4px; }
        .matrix-wrap { overflow-x: auto; }
        table.matrix th, table.matrix td { text-align: center; min-width: 64px; padding: 6px 8px; }
        table.matrix th { background: #f8f9fa; position: sticky; left: 0; z-index: 1; }
        table.matrix tbody th { background: #fafbfc; }
        """;
}
