namespace ObjectAnalyzer.Abstractions;

/// <summary>Immutable host ports for one command runner. There is no process-wide current instance.</summary>
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
