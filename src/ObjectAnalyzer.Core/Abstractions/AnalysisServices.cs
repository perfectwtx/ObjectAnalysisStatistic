namespace ObjectAnalyzer.Abstractions;

/// <summary>
/// 分析运行期服务集合：把控制台输出、进度渲染、结果呈现三类"宿主相关能力"
/// 从 Core 的静态全局依赖（Spectre.Console.AnsiConsole）中解耦出来。
///
/// 组合根（CLI / API / Web / 测试）各自注入合适的实现：
/// - CLI：Spectre 富渲染（进度条 / 表格 / 面板）；
/// - API / Web：静默或结构化推送；
/// - 测试：静默实现，从而摆脱"并发访问 AnsiConsole"的串行约束。
/// </summary>
public sealed class AnalysisServices
{
    /// <summary>控制台输出（默认静默）。</summary>
    public IAnalysisConsole Console { get; init; } = SilentConsole.Instance;

    /// <summary>进度渲染（默认直接执行、无 UI）。</summary>
    public IProgressReporter Progress { get; init; } = DirectProgressReporter.Instance;

    /// <summary>结果呈现（默认不呈现）。</summary>
    public IResultPresenter Presenter { get; init; } = NullResultPresenter.Instance;

    /// <summary>全静默服务（API / Web / 测试默认）。</summary>
    public static AnalysisServices Silent { get; } = new();
}
