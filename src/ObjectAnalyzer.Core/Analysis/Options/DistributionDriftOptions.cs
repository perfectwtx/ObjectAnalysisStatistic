namespace ObjectAnalyzer.Analysis.Options;

/// <summary>
/// 分布漂移：t-digest + HLL 草图导出 / 与历史基线做漂移对比。
/// </summary>
public sealed class DistributionDriftOptions
{
    /// <summary>分布快照导出路径（--export-distribution-snapshot）。</summary>
    public string? DistributionSnapshotPath { get; set; }

    /// <summary>与历史分布快照做 diff 的基线文件路径（--diff-distribution）。</summary>
    public string? DiffDistributionBaseline { get; set; }

    /// <summary>分布 diff 结果导出路径（--distribution-diff-report）。</summary>
    public string? DistributionDiffReportPath { get; set; }
}
