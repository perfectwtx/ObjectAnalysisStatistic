namespace ObjectAnalyzer.Reporting;

public static partial class MarkdownReportExporter
{
    private static string EscapeMd(string s)
    {
        // 先转义 < > 防止行内 HTML 注入，再插入换行 <br>
        return s.Replace("<", "&lt;").Replace(">", "&gt;")
            .Replace("|", "\\|").Replace("\r", " ").Replace("\n", "<br>");
    }

    private static string Truncate(string s, int maxLen)
    {
        return s.Length <= maxLen ? s : s[..(maxLen - 3)] + "...";
    }
}
