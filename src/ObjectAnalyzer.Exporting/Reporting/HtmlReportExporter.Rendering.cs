using System.Text;

namespace ObjectAnalyzer.Reporting;

public static partial class HtmlReportExporter
{
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
