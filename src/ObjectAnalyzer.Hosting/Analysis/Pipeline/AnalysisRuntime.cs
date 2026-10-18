using ObjectAnalyzer.Parsing;

namespace ObjectAnalyzer.Analysis.Pipeline;

/// <summary>Built-in composition. Applications can instead construct an executor with their own input ports.</summary>
public static class AnalysisRuntime
{
    public static AnalysisExecutor CreateExecutor(IProgressReporter? progress = null)
        => new(new DefaultAnalysisInputServices(), progress);
}
