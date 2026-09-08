namespace ObjectAnalyzer.Abstractions;

/// <summary>
/// 直接执行进度报告器：不渲染任何进度 UI，直接执行工作委托。
/// 用于 API / Web / 测试等无控制台场景。
/// </summary>
public sealed class DirectProgressReporter : IProgressReporter
{
    public static readonly DirectProgressReporter Instance = new();

    public void Run(string description, long? maxObjects, bool showProgress, Action<IProgress<long>?> work)
    {
        work(null);
    }
}
