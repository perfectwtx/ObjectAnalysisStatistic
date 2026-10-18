namespace ObjectAnalyzer.Reporting;

/// <summary>Compatibility entry point; all delivery projection is owned by ResultProjection.</summary>
public static class PiiRedactor
{
    public static AnalysisResult Redact(AnalysisResult result, bool enabled = true)
        => enabled ? ResultProjection.Redact(result, PiiPolicy.Resolve(result)) : result;
    public static AnalysisResult Redact(AnalysisResult result, PiiPolicy policy)
        => ResultProjection.Redact(result, policy);
}
