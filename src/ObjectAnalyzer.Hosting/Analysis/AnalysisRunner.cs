namespace ObjectAnalyzer.Analysis;

/// <summary>Silent convenience entry. Hosts supply dependencies through AnalysisCommandRunner.</summary>
public static class AnalysisRunner
{
    public static int Run(AnalysisOptions options, CancellationToken cancellationToken = default)
        => new AnalysisCommandRunner().Run(options, cancellationToken);
}
