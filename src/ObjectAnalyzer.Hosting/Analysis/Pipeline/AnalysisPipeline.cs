namespace ObjectAnalyzer.Analysis.Pipeline;

/// <summary>Stateless convenience adapter using silent, independent execution dependencies.</summary>
public static class AnalysisPipeline
{
    public static PipelineResult Execute(AnalysisOptions options, AnalysisRules? rules,
        AnalysisRunContext? runContext, CancellationToken cancellationToken = default)
        => AnalysisRuntime.CreateExecutor().Execute(options, rules, runContext, cancellationToken);

    public static string ResolveEffectiveFormat(string inputPath, AnalysisRules? rules, string? formatOverride)
        => DefaultAnalysisInputServices.ResolveEffectiveFormat(inputPath, rules, formatOverride);
    public static bool ResolveFlatten(AnalysisOptions options) => AnalysisSession.ResolveFlatten(options);
    internal static string CreateExecutionSignature(AnalysisOptions options) => AnalysisSession.CreateExecutionSignature(options);
}
