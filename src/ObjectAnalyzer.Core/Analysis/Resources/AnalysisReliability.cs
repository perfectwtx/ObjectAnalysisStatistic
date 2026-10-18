namespace ObjectAnalyzer.Analysis;

public enum AnalysisAccuracy { Exact, Approximate, Truncated, Sampled }
public sealed record AnalysisLimitation(string Metric, AnalysisAccuracy Accuracy, string Reason);

/// <summary>No raw input values. Stable evidence about which conclusions are complete, estimated or bounded.</summary>
public sealed class AnalysisReliability
{
    public List<AnalysisLimitation> Limitations { get; set; } = new();
    public string Summary => string.Join("; ", Limitations.Select(item => $"{item.Metric}: {item.Accuracy} ({item.Reason})"));
}

public enum AnalysisAccessMode { SinglePass, BufferedInput, SeekableInput, BoundedSample, BoundedState }
public sealed record AnalysisCapabilityRequirement(string Capability, AnalysisAccessMode Mode, long? Limit = null);
public sealed record AnalysisExecutionProfile(IReadOnlyList<AnalysisCapabilityRequirement> Capabilities, int BatchSize, int MaxWorkers);
