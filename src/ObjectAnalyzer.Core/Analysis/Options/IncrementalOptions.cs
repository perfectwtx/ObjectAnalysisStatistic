namespace ObjectAnalyzer.Analysis.Options;

/// <summary>
/// P6 增量分析：检查点落盘 / 断点续跑 / 历史基线合并。
/// </summary>
public sealed class IncrementalOptions
{
    /// <summary>检查点输出路径（--checkpoint &lt;PATH&gt;）。成功完成分析后原子保存 v3 计算状态。</summary>
    public string? CheckpointPath { get; set; }

    /// <summary>从检查点恢复分析（--resume &lt;PATH&gt;）。</summary>
    public string? ResumeFrom { get; set; }

    /// <summary>历史 v3 状态快照路径（--baseline &lt;PATH&gt;）。合并独立批次；普通报告无法用于续算。</summary>
    public string? BaselinePath { get; set; }
}
