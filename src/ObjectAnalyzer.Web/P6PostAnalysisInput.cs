using global::ObjectAnalyzer.Analysis;

namespace ObjectAnalyzer.Web;

/// <summary>P6 后处理入参：所有依赖通过构造期注入，便于测试构造边界场景。</summary>
internal sealed class P6PostAnalysisInput
{
    /// <summary>本次 run 的报告；合并后的新投影通过 P6PostAnalysisResult 返回。</summary>
    public required AnalysisResult Result { get; init; }

    public AnalysisAccumulator? Accumulator { get; init; }
    public string? ExecutionSignature { get; init; }

    /// <summary>--resume 上传的 .snapshot 路径（绝对路径）。空 / 不存在则跳过。</summary>
    public string? ResumeFromPath { get; init; }

    /// <summary>--baseline 上传的 v3 状态快照路径（绝对路径）。空 / 不存在则跳过。</summary>
    public string? BaselinePath { get; init; }

    /// <summary>--checkpoint 用户在 UI 上填的文件名（经 <see cref="P6PostAnalysis.SanitizeCheckpointName"/> 规整后传入）。</summary>
    public string? CheckpointFileName { get; init; }

    /// <summary>当前数据源文件路径，写入快照的 SourcePath 字段。</summary>
    public string SourcePath { get; init; } = "";

    /// <summary>当前数据源格式（jsonl/csv/json/...），写入快照的 SourceFormat 字段。</summary>
    public string SourceFormat { get; init; } = "";

    /// <summary>检查点写入目录；null 时落到默认 %TEMP%/objectanalyzer-checkpoints（测试可注入临时目录）。</summary>
    public string? CheckpointDirectory { get; init; }
}
