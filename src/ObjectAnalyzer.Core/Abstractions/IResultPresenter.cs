namespace ObjectAnalyzer.Abstractions;

/// <summary>
/// 分析结果呈现抽象：将 ConsoleReporter（Spectre Table / Panel / Rule 富渲染）从 Core 中解耦。
/// CLI 提供 Spectre 富渲染实现；API / Web 提供静默或结构化输出实现。
/// </summary>
public interface IResultPresenter
{
    /// <summary>向用户呈现分析结果（控制台表格、Web 推送等）。</summary>
    void Present(AnalysisResult result, AnalysisOptions options);
}
