namespace ObjectAnalyzer.Abstractions;

/// <summary>
/// 控制台输出抽象：将 Spectre.Console 的 MarkupLine / WriteLine / Write 从 Core 中解耦。
/// CLI 提供 Spectre 富渲染实现；API / Web / 测试提供纯文本或静默实现。
/// 线程安全：实现方须自行保证并发安全（不再依赖全局 AnsiConsole 句柄）。
/// </summary>
public interface IAnalysisConsole
{
    /// <summary>输出一行带标记的文本（如 "[green]完成[/]"），实现方决定如何渲染标记。</summary>
    void MarkupLine(string markup);

    /// <summary>输出一行纯文本（默认空行）。</summary>
    void WriteLine(string text = "");

    /// <summary>输出文本（不换行）。</summary>
    void Write(string text);
}
