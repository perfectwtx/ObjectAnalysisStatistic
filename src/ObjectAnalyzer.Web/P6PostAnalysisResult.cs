namespace ObjectAnalyzer.Web;

/// <summary>P6 后处理返回值：仅承载「P6 步骤自己产生的新数据」，不与主流程的 <c>State</c> 耦合。</summary>
internal sealed class P6PostAnalysisResult
{
    public global::ObjectAnalyzer.Analysis.AnalysisResult? Result { get; set; }

    /// <summary>检查点写入的服务器路径（绝对）。未生成时为 null。</summary>
    public string? LastCheckpointServerPath { get; set; }

    /// <summary>检查点字节大小。</summary>
    public long LastCheckpointSize { get; set; }

    /// <summary>--resume 失败时的原始异常消息（待 Home.razor 用 Loc.Get 包装）。未失败时为 null。</summary>
    public string? ResumeErrorMessage { get; set; }

    /// <summary>--baseline 失败时的原始异常消息。</summary>
    public string? BaselineErrorMessage { get; set; }

    /// <summary>--checkpoint 失败时的原始异常消息。</summary>
    public string? CheckpointErrorMessage { get; set; }
}
