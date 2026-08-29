using global::ObjectAnalyzer.Analysis;

namespace ObjectAnalyzer.Web;

/// <summary>
/// P6.1 / P6.3 进度与内存预算的回调工厂。
/// 把 <c>IProgress&lt;long&gt;</c> 回调的内部实现从 Home.razor 拆出，
/// 依赖仅通过构造期注入（showProgress / budget / 文本写入 / 本地化函数），
/// 便于单元测试在不实例化 Blazor 组件的前提下覆盖分支。
/// </summary>
internal static class P6ProgressReporter
{
    /// <summary>
    /// 构建一个进度回调闭包：
    /// <list type="bullet">
    ///   <item>若 <paramref name="showProgress"/> = true，每次调用都把累计对象数按
    ///         <paramref name="localize"/>("ProgressAnalyzedN", count) 写入 <paramref name="progressTextSink"/>。</item>
    ///   <item>无论是否显示进度，每次都调用 <paramref name="budget"/>.Check——
    ///         内存预算是安全护栏，不能因为关掉进度条就绕过。</item>
    ///   <item>soft 模式触发自动采样时，调用 <paramref name="memoryWarningSink"/> 一次（只一次；
    ///         <see cref="MemoryBudget"/> 内部已用 _alreadyWarned 防抖）。</item>
    /// </list>
    /// </summary>
    public static Action<long> Build(
        bool showProgress,
        MemoryBudget budget,
        Action<string> progressTextSink,
        Action<string> memoryWarningSink,
        Func<string, object?[], string> localize)
    {
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(progressTextSink);
        ArgumentNullException.ThrowIfNull(memoryWarningSink);
        ArgumentNullException.ThrowIfNull(localize);

        return count =>
        {
            if (showProgress)
            {
                progressTextSink(localize("ProgressAnalyzedN", new object?[] { count }));
            }
            // 内存采样（不论是否显示进度都要做：内存预算是安全护栏）
            budget.Check(count, out bool samplingAutoEnable);
            if (samplingAutoEnable)
            {
                memoryWarningSink(localize("WarnMemorySoftExceeded", Array.Empty<object?>()));
            }
        };
    }
}
