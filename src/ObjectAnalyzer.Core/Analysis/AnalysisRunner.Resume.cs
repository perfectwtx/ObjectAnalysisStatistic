using ObjectAnalyzer.Analysis.Pipeline;

namespace ObjectAnalyzer.Analysis;

public static partial class AnalysisRunner
{
    private static void MergeIncrementalResults(AnalysisOptions options, PipelineResult pipeline,
        long startFromPosition, AnalysisRunContext runContext)
    {
        if (string.IsNullOrEmpty(options.Incremental.BaselinePath) && string.IsNullOrEmpty(options.Incremental.CheckpointPath)) return;
        var state = pipeline.Accumulator ?? throw new InvalidOperationException("Calculation state is missing.");
        if (!string.IsNullOrEmpty(options.Incremental.BaselinePath))
        {
            // A report is a projection and cannot reconstruct samples, moments or sketches.
            var baseline = AnalyzerSnapshot.LoadFrom(options.Incremental.BaselinePath);
            if (baseline.ExecutionSignature is not null && baseline.ExecutionSignature != BuildExecutionSignature(options))
                throw new InvalidOperationException("Baseline pipeline configuration differs from the current run.");
            state.Merge(baseline.State.Restore());
            var completed = state.CompleteDetailed();
            pipeline.Result = completed.Result;
            pipeline.QuantileSketches = completed.QuantileSketches;
            pipeline.DistinctSketches = completed.DistinctSketches;
        }
        if (!string.IsNullOrEmpty(options.Incremental.CheckpointPath))
        {
            new AnalyzerSnapshot
            {
                SourcePath = options.Input.InputPath,
                SourceFormat = runContext.EffectiveFormat ?? "",
                SourcePosition = startFromPosition + runContext.LastSourcePosition,
                SourceFingerprint = ComputeSourceFingerprint(options.Input.InputPath, startFromPosition + runContext.LastSourcePosition),
                PositionKind = "line",
                ExecutionSignature = BuildExecutionSignature(options),
                State = state.Capture()
            }.SaveTo(options.Incremental.CheckpointPath);
        }
    }
    private static string BuildExecutionSignature(AnalysisOptions options) => AnalysisPipeline.CreateExecutionSignature(options);

    private static void ValidateCheckpointOptions(AnalysisOptions options)
    {
        if (!string.IsNullOrEmpty(options.Input.ConnectionString)
            || CompressionDetector.Detect(options.Input.InputPath) != CompressionType.None
            || AnalysisPipeline.ResolveEffectiveFormat(options.Input.InputPath, null, options.Input.Format) != "jsonl")
            throw new NotSupportedException("Position-based checkpoints currently require uncompressed JSONL input; other formats need an explicit source cursor.");
        if (options.Sampling.SampleReservoir > 0 || options.Sampling.SampleStep > 0
            || options.Memory.MaxMemoryMB > 0 && options.Memory.MaxMemoryMode == MaxMemoryMode.Soft)
            throw new NotSupportedException("Checkpoint/resume cannot combine with sampling or automatic memory fallback.");
        if (!string.IsNullOrWhiteSpace(options.Input.RootPath)) throw new NotSupportedException("Checkpoint/resume does not support root projection.");
        new AnalysisAccumulator(features: options.Advanced).EnsureMergeable();
    }

    private static string ComputeSourceFingerprint(string path, long lines)
    {
        if (lines < 0) throw new InvalidDataException("Negative source position.");
        using var reader = new StreamReader(path);
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        for (long i = 0; i < lines; i++)
        {
            string line = reader.ReadLine() ?? throw new InvalidDataException("Source is shorter than its saved cursor.");
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(line + "\n"));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
