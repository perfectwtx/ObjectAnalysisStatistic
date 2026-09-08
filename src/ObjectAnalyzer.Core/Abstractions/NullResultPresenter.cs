namespace ObjectAnalyzer.Abstractions;

/// <summary>
/// 静默结果呈现器：不做任何呈现。用于 API / Web / 测试。
/// </summary>
public sealed class NullResultPresenter : IResultPresenter
{
    public static readonly NullResultPresenter Instance = new();

    public void Present(AnalysisResult result, AnalysisOptions options) { }
}
