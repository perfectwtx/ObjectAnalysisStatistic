namespace ObjectAnalyzer.Analysis.Options;

/// <summary>
/// 进度显示：Spectre.Console 进度条 + ETA。默认开启；CI / 静默流水线可关。
/// </summary>
public sealed class ProgressOptions
{
    /// <summary>是否在分析过程中显示 Spectre.Console 进度条 + ETA。默认 true。关闭后跳过进度条渲染，输出直接打印到 stdout。</summary>
    public bool ShowProgress { get; set; } = true;
}
