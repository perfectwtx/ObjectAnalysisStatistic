using System.Text.Json;

namespace ObjectAnalyzer.Platform;

/// <summary>Projects a detached job for delivery without modifying historical storage.</summary>
public static class JobResultProjection
{
    public static AnalysisJob Job(AnalysisJob source)
    {
        var copy = AnalysisAccumulator.Clone(source);
        if (!string.IsNullOrWhiteSpace(copy.ResultJson))
        {
            try { copy.ResultJson = JsonSerializer.Serialize(ResultProjection.ReadStored(copy.ResultJson)); }
            catch (JsonException) { copy.ResultJson = null; }
        }
        return copy;
    }

}
