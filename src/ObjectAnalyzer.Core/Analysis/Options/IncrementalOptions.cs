namespace ObjectAnalyzer.Analysis.Options;

/// <summary>
/// P6 增量分析：检查点落盘 / 断点续跑 / 历史基线合并。
/// </summary>
public sealed class IncrementalOptions
{
    /// <summary>检查点输出路径（--checkpoint &lt;PATH&gt;）。每 10K 对象落盘一次 AnalyzerSnapshot。</summary>
    public string? CheckpointPath { get; set; }

    /// <summary>从检查点恢复分析（--resume &lt;PATH&gt;）。</summary>
    public string? ResumeFrom { get; set; }

    /// <summary>历史 AnalysisResult JSON 路径（--baseline &lt;PATH&gt;），作为本次分析的基线合并对象。</summary>
    public string? BaselinePath { get; set; }
}
