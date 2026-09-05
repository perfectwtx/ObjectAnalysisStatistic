namespace ObjectAnalyzer.Abstractions;

/// <summary>
/// 进度报告抽象：将 Spectre.Console 的 Progress() / Status() 从 Core 中解耦。
/// CLI 提供 Spectre 进度条 / spinner 实现；API / Web / 测试提供直接执行实现。
/// </summary>
public interface IProgressReporter
{
    /// <summary>
    /// 执行分析工作，可选地显示进度 UI。
    /// </summary>
    /// <param name="description">任务描述（用于进度条标题或 spinner 文本）。</param>
    /// <param name="maxObjects">预估总对象数（null 表示未知，进度条退化为计数模式）。</param>
    /// <param name="showProgress">是否显示进度条（false 时实现方可选择 spinner 或无 UI）。</param>
    /// <param name="work">
    /// 实际分析工作。接收一个 <see cref="IProgress{T}"/> 用于上报已处理对象数
    /// （实现方保证回调线程安全）；不需要进度上报时可能传入 null。
    /// </param>
    void Run(string description, long? maxObjects, bool showProgress, Action<IProgress<long>?> work);
}
