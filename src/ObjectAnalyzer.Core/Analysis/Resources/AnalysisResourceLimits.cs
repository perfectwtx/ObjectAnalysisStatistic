namespace ObjectAnalyzer.Analysis;

/// <summary>Finite admission and retention limits. Units are explicit; these are not a process RSS guarantee.</summary>
public sealed record AnalysisResourceLimits
{
    public int MaxPathCharacters { get; init; } = 1024;
    public int MaxRulesPerField { get; init; } = 32;
    public int MaxAnalyzerComponents { get; init; } = 32;
    public int MaxFields { get; init; } = 512;
    public int MaxObjectIdentities { get; init; } = 100_000;
    public int MaxObjectSamples { get; init; } = 256;
    public long MaxRetainedPayloadBytes { get; init; } = 64L * 1024 * 1024;
    public int MaxFrequencyValueBytes { get; init; } = 16 * 1024;
    public int MaxSampleValueBytes { get; init; } = 4096;
    public int MaxRuleSamples { get; init; } = 5;
    public long MaxSamplingBytes { get; init; } = 64L * 1024 * 1024;
    public int MaxPreviewRecords { get; init; } = 100;
    public long MaxPreviewBytes { get; init; } = 1024 * 1024;
    public int MaxRecordCharacters { get; init; } = 1024 * 1024;
    public long MaxBufferedInputBytes { get; init; } = 64L * 1024 * 1024;
    public int BatchSize { get; init; } = 1024;
    public long MaxBatchBytes { get; init; } = 8L * 1024 * 1024;
    public int MaxWorkers { get; init; } = 8;
    public int MaxTimeBins { get; init; } = 4096;
    public int MaxPatternTemplates { get; init; } = 256;
    public int MaxPatternCharacters { get; init; } = 4096;
    public int MaxExampleCharacters { get; init; } = 256;
    public int MaxCorrelationFields { get; init; } = 32;
    public int MaxCorrelationRows { get; init; } = 20_000;
    public int MaxFuzzyKeys { get; init; } = 5000;
    public int MaxRelationshipPaths { get; init; } = 1024;
    public int MaxDiagnosticSamples { get; init; } = 100;
    public int AsyncQueueCapacity { get; init; } = 128;

    internal void Validate()
    {
        foreach (var property in typeof(AnalysisResourceLimits).GetProperties())
        {
            long value = Convert.ToInt64(property.GetValue(this), System.Globalization.CultureInfo.InvariantCulture);
            if (value < 0) throw new ArgumentException($"Resource limit {property.Name} cannot be negative.");
        }
        if (BatchSize == 0 || MaxBatchBytes == 0 || MaxWorkers == 0 || MaxRecordCharacters == 0
            || MaxBufferedInputBytes == 0 || AsyncQueueCapacity == 0 || MaxFields == 0
            || MaxCorrelationRows == 0 || MaxCorrelationFields == 0 || MaxPatternCharacters == 0)
            throw new ArgumentException("Execution capacities and input admission limits must be positive.");
    }
}

public sealed class AnalysisResourceLimitException(string resource, long limit)
    : InvalidOperationException($"Analysis resource '{resource}' exceeded its limit ({limit}).")
{
    public string Resource { get; } = resource;
    public long Limit { get; } = limit;
}

internal sealed class RetainedPayloadBudget(long limit)
{
    private long _bytes;
    public long Bytes => Interlocked.Read(ref _bytes);
    public void Reserve(long bytes)
    {
        long current = Interlocked.Add(ref _bytes, bytes);
        if (current <= limit && current >= 0) return;
        Interlocked.Add(ref _bytes, -bytes);
        throw new AnalysisResourceLimitException("retained-payload-bytes", limit);
    }
    public void Release(long bytes) => Interlocked.Add(ref _bytes, -bytes);
    public static long Size(RecordValue value) => 96L + value.CanonicalKey.Length * 2L;
}
