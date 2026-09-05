namespace ObjectAnalyzer.Abstractions;

/// <summary>
/// 控制台标记文本辅助。Core 不再依赖 Spectre.Console，
/// 但产出的字符串仍采用 <c>[green]...[/]</c> 风格的标记语法，
/// 由 <see cref="IAnalysisConsole"/> 的具体实现（CLI 的 Spectre 渲染器 / 纯文本剥离器）解释。
/// 用户数据（文件路径、字段名等）嵌入标记前必须经 <see cref="Escape"/> 转义，
/// 避免其中的 <c>[</c> <c>]</c> 被误当作样式标记。
/// </summary>
public static class MarkupText
{
    /// <summary>转义 <c>[</c> 与 <c>]</c>（与 Spectre.Console 的 Markup.Escape 语义一致）。</summary>
    public static string Escape(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return text.Replace("[", "[[").Replace("]", "]]");
    }
}
