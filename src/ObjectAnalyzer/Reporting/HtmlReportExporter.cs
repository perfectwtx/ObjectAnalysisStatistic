using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Spectre.Console;

namespace ObjectAnalyzer;

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
            }
            if (stat.MinLength.HasValue && stat.MaxLength.HasValue)
            {
                metricInfo += $"<div>长度: {stat.MinLength} ~ {stat.MaxLength} 字符</div>";
            }
            if (string.IsNullOrEmpty(metricInfo)) metricInfo = "<span class=\"na\">—</span>";

            string types = string.Join(", ", stat.TypeCounts.Select(t => $"{t.Key}({t.Value})"));
            string topValues = string.Join(", ", stat.ValueCounts
                .OrderByDescending(v => v.Value).Take(3)
                .Select(v => $"{Escape(Truncate(v.Key, 30))} ×{v.Value.ToString(CultureInfo.InvariantCulture)}"));

            string coveragePct = (stat.Coverage * 100).ToString("F1", CultureInfo.InvariantCulture);
            string ruleInfo = stat.AppliedRule != null ? "<span class=\"rule-badge\">已应用</span>" : "";
            if (stat.EnumViolations.Count > 0)
            {
                var violations = string.Join(", ", stat.EnumViolations.Select(v => $"<span class=\"violation\">{Escape(v)}</span>"));
                ruleInfo += $"<div class=\"violations\">枚举违规: {violations}</div>";
            }

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
        .violations { margin-top: 4px; font-size: 0.8rem; color: #842029; }
        .violation { background: #f8d7da; color: #842029; padding: 1px 4px; border-radius: 3px; margin: 0 2px; }
        .quality-pass { color: #15803d; font-weight: 600; }
        .quality-fail { color: #b91c1c; font-weight: 600; }
        """;
}
