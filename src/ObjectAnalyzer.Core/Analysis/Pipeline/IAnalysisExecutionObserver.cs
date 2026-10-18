namespace ObjectAnalyzer.Analysis.Pipeline;

/// <summary>
/// 分析管线生命周期观察器。
/// 观察器属于宿主扩展点，Core 不依赖具体的 Job、数据库或 Web 实现。
/// 同步通知；回调异常记录在 AnalysisRunContext.ObserverErrors，不改变计算结果或终止事件。
/// </summary>
public interface IAnalysisExecutionObserver
{
    /// <summary>分析开始。</summary>
    void OnStarted();

    /// <summary>分析成功完成。</summary>
    void OnCompleted(PipelineResult result);

    /// <summary>分析被取消。</summary>
    void OnCancelled();

    /// <summary>分析失败。</summary>
    void OnFailed(Exception exception);
}
