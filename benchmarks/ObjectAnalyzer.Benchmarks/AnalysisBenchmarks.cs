using BenchmarkDotNet.Attributes;
using ObjectAnalyzer.Analysis;
using ObjectAnalyzer.TestData;
using System.Text.Json;

namespace ObjectAnalyzer.Benchmarks;

[MemoryDiagnoser]
public class AnalysisBenchmarks
{
    [Params(10000)] public int Records { get; set; }
    [Params(32, 4096)] public int Cardinality { get; set; }
    [Params(1, 4)] public int Parallelism { get; set; }
    private Dictionary<string, object?>[] _records = [];

    [GlobalSetup] public void Setup() => _records = DeterministicRecords.Create(Records, Cardinality);
    [Benchmark] public AnalysisResult ObserveAndComplete()
    {
        var state = new AnalysisAccumulator(maxTrackedValues: 128);
        state.ObserveRange(_records, new ParallelOptions { MaxDegreeOfParallelism = Parallelism });
        return state.Complete();
    }
}

[MemoryDiagnoser]
public class CheckpointBenchmarks
{
    private AnalysisStateSnapshot _snapshot = null!;
    [GlobalSetup] public void Setup()
    {
        var state = new AnalysisAccumulator(maxTrackedValues: 128);
        state.ObserveRange(DeterministicRecords.Create(10000, 4096), new ParallelOptions { MaxDegreeOfParallelism = 1 });
        _snapshot = state.Capture();
    }
    [Benchmark] public AnalysisResult SerializeRestoreComplete()
        => JsonSerializer.Deserialize<AnalysisStateSnapshot>(JsonSerializer.Serialize(_snapshot))!.Restore().Complete();
}
