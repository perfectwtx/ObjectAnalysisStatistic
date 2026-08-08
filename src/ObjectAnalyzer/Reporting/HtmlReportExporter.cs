using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Spectre.Console;

namespace ObjectAnalyzer;

public static class HtmlReportExporter
{
    public static void Save(AnalysisResult result, string outputPath)
    {
        AnsiConsole.MarkupLine($"正在将 HTML 报告保存至: [blue]{Markup.Escape(outputPath)}[/] ...");
        string html = GenerateHtml(result);
        File.WriteAllText(outputPath, html, Encoding.UTF8);
        AnsiConsole.MarkupLine($"[green]HTML 报告成功保存！[/]");
    }

    private static string GenerateHtml(AnalysisResult result)
    {
        var sb = new StringBuilder();
        const string title = "ObjectAnalyzer 分析报告";
        string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        // 准备图表数据
        var fieldNames = result.FieldStatistics.Select(f => EscapeJs(f.FieldName)).ToArray();
        var coverages = result.FieldStatistics.Select(f => f.Coverage.ToString("F1", CultureInfo.InvariantCulture)).ToArray();

        var topFields = result.FieldStatistics.Take(10).ToList();

        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"zh-CN\">");
        sb.AppendLine("<head>");
        sb.AppendLine("<meta charset=\"UTF-8\">");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
        sb.Append("<title>").Append(title).AppendLine("</title>");
        sb.AppendLine("<script src=\"https://cdn.jsdelivr.net/npm/chart.js@4\"></script>");
        sb.AppendLine("<style>");
        sb.AppendLine(CssStyles);
        sb.AppendLine("</style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");

        // 头部
        sb.AppendLine("<div class=\"header\">");
        sb.Append("<h1>").Append(title).AppendLine("</h1>");
        sb.Append("<p class=\"timestamp\">生成时间: ").Append(timestamp).AppendLine("</p>");
        sb.AppendLine("</div>");

        // 摘要卡片
        sb.AppendLine("<div class=\"cards\">");
        sb.Append(Card("总对象数", result.TotalObjects.ToString(CultureInfo.InvariantCulture), "blue"));
        sb.Append(Card("唯一对象数", result.TotalUniqueObjects.ToString(CultureInfo.InvariantCulture), "green"));
        sb.Append(Card("重复组数", result.DuplicateObjects.Count.ToString(CultureInfo.InvariantCulture),
            result.DuplicateObjects.Count > 0 ? "orange" : "green"));
        sb.Append(Card("字段数", result.FieldStatistics.Count.ToString(CultureInfo.InvariantCulture), "purple"));
        sb.AppendLine("</div>");

        // 图表区域
        sb.AppendLine("<div class=\"charts\">");
        sb.AppendLine("<div class=\"chart-container\">");
        sb.AppendLine("<h2>字段覆盖率</h2>");
        sb.AppendLine("<canvas id=\"coverageChart\"></canvas>");
        sb.AppendLine("</div>");

        if (topFields.Count > 0)
        {
            sb.AppendLine("<div class=\"chart-container\">");
            sb.AppendLine("<h2>字段类型分布 (Top 10)</h2>");
            sb.AppendLine("<canvas id=\"typeChart\"></canvas>");
            sb.AppendLine("</div>");
        }
        sb.AppendLine("</div>");

        // 重复对象
        if (result.DuplicateObjects.Count > 0)
        {
            sb.AppendLine("<div class=\"section\">");
            sb.Append("<h2>重复对象 (").Append(result.DuplicateObjects.Count.ToString(CultureInfo.InvariantCulture)).AppendLine(" 组)</h2>");
            sb.AppendLine("<table><thead><tr><th>重复次数</th><th>对象内容</th></tr></thead><tbody>");
            foreach (var dup in result.DuplicateObjects)
            {
                sb.Append("<tr><td>").Append(dup.Count.ToString(CultureInfo.InvariantCulture))
                    .Append("</td><td><code>").Append(Escape(dup.ObjectJson)).AppendLine("</code></td></tr>");
            }
            sb.AppendLine("</tbody></table>");
            sb.AppendLine("</div>");
        }

        // 字段详情表
        sb.AppendLine("<div class=\"section\">");
        sb.AppendLine("<h2>字段详情</h2>");
        sb.AppendLine("<table><thead><tr><th>字段名</th><th>覆盖率</th><th>主导类型</th><th>默认值</th><th>唯一值数</th><th>规则</th><th>详情</th></tr></thead><tbody>");
        foreach (var stat in result.FieldStatistics)
        {
            string defaultVal = stat.DefaultValueRepresentation != null
                ? $"<code>{Escape(stat.DefaultValueRepresentation)}</code>"
                : "<span class=\"na\">—</span>";
            string types = string.Join(", ", stat.TypeCounts.Select(t => $"{t.Key}({t.Value})"));
            string topValues = string.Join(", ", stat.ValueCounts
                .OrderByDescending(v => v.Value).Take(3)
                .Select(v => $"{Escape(Truncate(v.Key, 30))} ×{v.Value.ToString(CultureInfo.InvariantCulture)}"));

            string coveragePct = (stat.Coverage * 100).ToString("F1", CultureInfo.InvariantCulture);
            string uniqueCount = stat.ValueCountsTruncated
                ? $"{stat.ValueCounts.Count.ToString(CultureInfo.InvariantCulture)}+"
                : stat.ValueCounts.Count.ToString(CultureInfo.InvariantCulture);
            string ruleInfo = stat.AppliedRule != null ? "<span class=\"rule-badge\">已应用</span>" : "";
            if (stat.EnumViolations.Count > 0)
            {
                var violations = string.Join(", ", stat.EnumViolations.Select(v => $"<span class=\"violation\">{Escape(v)}</span>"));
                ruleInfo += $"<div class=\"violations\">枚举违规: {violations}</div>";
            }

            sb.Append("<tr>")
                .Append("<td><code>").Append(Escape(stat.FieldName)).Append("</code></td>")
                .Append("<td>").Append(stat.Count.ToString(CultureInfo.InvariantCulture)).Append('/').Append(result.TotalObjects.ToString(CultureInfo.InvariantCulture))
                .Append(" (").Append(coveragePct).Append("%)</td>")
                .Append("<td>").Append(Escape(types)).Append("</td>")
                .Append("<td>").Append(defaultVal).Append("</td>")
                .Append("<td>").Append(uniqueCount).Append("</td>")
                .Append("<td>").Append(ruleInfo).Append("</td>")
                .Append("<td class=\"detail\">").Append(topValues).Append("</td>")
                .AppendLine("</tr>");
        }
        sb.AppendLine("</tbody></table>");
        sb.AppendLine("</div>");

        // Chart.js 脚本
        sb.AppendLine("<script>");

        // 覆盖率图表
        sb.AppendLine("new Chart(document.getElementById('coverageChart'), {");
        sb.AppendLine("  type: 'bar',");
        sb.Append("  data: { labels: [").Append(string.Join(",", fieldNames.Select(n => $"'{n}'"))).AppendLine("],");
        sb.Append("    datasets: [{ label: '覆盖率 (%)', data: [").Append(string.Join(",", coverages)).AppendLine("],");
        sb.AppendLine("      backgroundColor: 'rgba(54,162,235,0.6)', borderColor: 'rgba(54,162,235,1)', borderWidth: 1 }]");
        sb.AppendLine("  },");
        sb.AppendLine("  options: { responsive: true, scales: { y: { beginAtZero: true, max: 100 } }, plugins: { legend: { display: false } } }");
        sb.AppendLine("});");

        // 类型分布图表
        if (topFields.Count > 0)
        {
            var allTypes = topFields.SelectMany(f => f.TypeCounts.Keys).Distinct().ToList();
            var colors = new[] { "rgba(54,162,235,0.7)", "rgba(255,99,132,0.7)", "rgba(75,192,192,0.7)",
                "rgba(255,205,86,0.7)", "rgba(153,102,255,0.7)", "rgba(255,159,64,0.7)" };

            sb.AppendLine("new Chart(document.getElementById('typeChart'), {");
            sb.AppendLine("  type: 'bar',");
            sb.Append("  data: { labels: [").Append(string.Join(",", topFields.Select(f => $"'{EscapeJs(f.FieldName)}'"))).AppendLine("],");
            sb.Append("    datasets: [");
            for (int i = 0; i < allTypes.Count; i++)
            {
                string t = allTypes[i];
                string color = colors[i % colors.Length];
                var data = topFields.Select(f => f.TypeCounts.GetValueOrDefault(t, 0).ToString(CultureInfo.InvariantCulture));
                sb.Append(CultureInfo.InvariantCulture, $"{{ label: '{EscapeJs(t)}', data: [{string.Join(",", data)}], backgroundColor: '{color}' }},");
            }
            sb.AppendLine("] },");
            sb.AppendLine("  options: { responsive: true, scales: { x: { stacked: true }, y: { stacked: true } } }");
            sb.AppendLine("});");
        }

        sb.AppendLine("</script>");
        sb.AppendLine("</body></html>");

        return sb.ToString();
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

    // 用于嵌入 JS 字符串字面量：转义为 \uXXXX 形式可安全放入单引号字符串，
    // 并防止 </script> 提前关闭脚本块。
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
        .rule-badge { display: inline-block; background: #e7f1ff; color: #1b6ec2; padding: 2px 8px; border-radius: 4px; font-size: 0.8rem; }
        .violations { margin-top: 4px; font-size: 0.8rem; color: #842029; }
        .violation { background: #f8d7da; color: #842029; padding: 1px 4px; border-radius: 3px; margin: 0 2px; }
        """;
}
