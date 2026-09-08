using ObjectAnalyzer.Abstractions;
using Spectre.Console;

namespace ObjectAnalyzer;

// ── CLI 侧宿主端口实现 ──
// 启动时注入 AnalysisServices，把 Core 的过程消息 / 进度 / 结果呈现
// 落到 Spectre.Console 的富渲染上。

/// <summary>控制台端口实现：直接转发 AnsiConsole（保留 Spectre 标记渲染）。</summary>
internal sealed class SpectreConsoleAdapter : IAnalysisConsole
{
    public void MarkupLine(string markup) => AnsiConsole.MarkupLine(markup);
    public void WriteLine(string text = "") => AnsiConsole.WriteLine(text);
    public void Write(string text) => AnsiConsole.Write(text);
}

/// <summary>进度端口实现：Spectre 进度条（未知总量时退化为 spinner + 计数）。</summary>
internal sealed class SpectreProgressReporter : IProgressReporter
{
    public void Run(string description, long? maxObjects, bool showProgress, Action<IProgress<long>?> work)
    {
        if (!showProgress)
        {
            // 无进度 UI：spinner + 计数
            AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(Style.Parse("green"))
                .Start(description, _ => work(null));
            return;
        }

        AnsiConsole.Progress()
            .AutoClear(false)
            .HideCompleted(false)
            .Columns(
                new TaskDescriptionColumn { Alignment = Justify.Left },
                new ProgressBarColumn(),
                new PercentageColumn(),
                new RemainingTimeColumn(),
                new SpinnerColumn(Spinner.Known.Dots))
            .Start(ctx =>
            {
                // 任务描述为纯文本（端口契约）；由本实现负责转义
                var task = ctx.AddTask(MarkupText.Escape(description), maxValue: maxObjects ?? 100);
                work(new Progress<long>(value =>
                {
                    task.Value = value;
                    // 未知总量时 task.Value 可能停在 < maxValue；work 完成后由下方收尾
                }));
                // 确保 100% 收尾（未知总量时 task.Value 可能停在 < maxValue 处）
                if (task.Value < task.MaxValue) task.Value = task.MaxValue;
            });
    }
}
