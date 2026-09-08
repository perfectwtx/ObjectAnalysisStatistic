namespace ObjectAnalyzer.Abstractions;

/// <summary>
/// 静默控制台：丢弃所有输出。用于 API / Web / 测试等不需要控制台反馈的场景。
/// </summary>
public sealed class SilentConsole : IAnalysisConsole
{
    public static readonly SilentConsole Instance = new();

    public void MarkupLine(string markup) { }
    public void WriteLine(string text = "") { }
    public void Write(string text) { }
}

/// <summary>
/// 纯文本控制台：剥离标记后写入 <see cref="TextWriter"/>（默认 stdout）。
/// 用于不需要富渲染但需要日志留痕的场景（如 CI 管道）。
/// </summary>
public sealed class PlainTextConsole(TextWriter? writer = null) : IAnalysisConsole
{
    private readonly TextWriter _writer = writer ?? Console.Out;
    private readonly object _lock = new();

    public void MarkupLine(string markup)
    {
        lock (_lock) _writer.WriteLine(StripMarkup(markup));
    }

    public void WriteLine(string text = "")
    {
        lock (_lock) _writer.WriteLine(text);
    }

    public void Write(string text)
    {
        lock (_lock) _writer.Write(text);
    }

    /// <summary>剥离 Spectre 风格标记（[green]...[/] 等），保留纯文本。</summary>
    internal static string StripMarkup(string markup)
    {
        if (string.IsNullOrEmpty(markup)) return markup;
        var sb = new System.Text.StringBuilder(markup.Length);
        int i = 0;
        while (i < markup.Length)
        {
            if (markup[i] == '[')
            {
                int close = markup.IndexOf(']', i + 1);
                // [/] 或 [style] 形式：跳过整个标记；找不到闭合括号则按普通字符处理
                if (close > i && LooksLikeMarkupTag(markup.AsSpan(i + 1, close - i - 1)))
                {
                    i = close + 1;
                    continue;
                }
            }
            sb.Append(markup[i]);
            i++;
        }
        return sb.ToString();
    }

    private static bool LooksLikeMarkupTag(ReadOnlySpan<char> tag)
    {
        if (tag.IsEmpty) return true; // [] 视为空标记
        var t = tag;
        if (t[0] == '/') t = t[1..];
        // 标记内容形如 "green"、"bold red"、"blue on white"；逐段校验（空格分隔）
        while (!t.IsEmpty)
        {
            int space = t.IndexOf(' ');
            ReadOnlySpan<char> part = space < 0 ? t : t[..space];
            if (!part.IsEmpty && !IsKnownToken(part)) return false;
            if (space < 0) break;
            t = t[(space + 1)..];
        }
        return true;
    }

    private static bool IsKnownToken(ReadOnlySpan<char> token)
    {
        // 宽松匹配：标记词由字母/数字/下划线/连字符/# 组成（颜色名 / bold / italic / #RRGGBB）
        foreach (char c in token)
        {
            if (!(char.IsLetterOrDigit(c) || c is '_' or '-' or '#'))
                return false;
        }
        return !token.IsEmpty;
    }
}
