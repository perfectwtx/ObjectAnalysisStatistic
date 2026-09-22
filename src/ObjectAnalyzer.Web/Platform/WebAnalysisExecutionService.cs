using ObjectAnalyzer.Analysis;
using ObjectAnalyzer.Analysis.Pipeline;
using ObjectAnalyzer.Platform;

namespace ObjectAnalyzer.Web.Platform;

/// <summary>
/// Web 宿主的统一分析执行适配器。
/// 将 UI 需要的 PipelineResult 与平台 Job 生命周期桥接起来，避免 Razor 页面直接管理 Job。
/// </summary>
public sealed class WebAnalysisExecutionService
{
    private readonly AnalysisExecutionCoordinator _coordinator;

    public WebAnalysisExecutionService(AnalysisExecutionCoordinator coordinator)
    {
        _coordinator = coordinator;
    }

    /// <summary>
    /// 执行一次分析，并由平台 Coordinator 统一记录 Job、质量分数、Issue、告警以及实际门禁结果。
    /// </summary>
    public async Task<PipelineResult> ExecuteAsync(
        string sourceName,
        string sourceType,
        AnalysisOptions options,
        AnalysisRules? rules,
        AnalysisRunContext runContext,
        Action<PipelineResult>? beforePersist = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(runContext);

        PipelineResult? pipelineResult = null;

        await _coordinator.ExecuteAsync(
            sourceName,
            sourceType,
            async token =>
            {
                pipelineResult = await Task.Run(
                    () => AnalysisPipeline.Execute(options, rules, runContext, token),
                    token);

                // 允许 Web 宿主在 Coordinator 持久化 Job 之前完成结果后处理。
                // 这样诸如 resume / baseline merge 得到的最终 Result 会进入同一个 AnalysisJob。
                beforePersist?.Invoke(pipelineResult);

                return pipelineResult.Result;
            },
            options,
            cancellationToken);

        return pipelineResult
            ?? throw new InvalidOperationException("分析执行器未返回 PipelineResult。");
    }
}
