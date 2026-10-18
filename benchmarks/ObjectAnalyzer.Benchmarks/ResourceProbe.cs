using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using ObjectAnalyzer.Analysis;
using ObjectAnalyzer.TestData;

namespace ObjectAnalyzer.Benchmarks;

/// <summary>One workload per process. Working set includes runtime and input; it is not retained model size.</summary>
internal static class ResourceProbe
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public static void Run(int cardinality, int parallelism, string output)
    {
        var records = DeterministicRecords.Create(10000, cardinality);
        string hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(records)));
        // Warm up separately; this remains a resource observation, not a statistical timing benchmark.
        var warmup = new AnalysisAccumulator(maxTrackedValues: 128);
        warmup.ObserveRange(records.Take(100), new ParallelOptions { MaxDegreeOfParallelism = parallelism });
        _ = warmup.Complete();
        warmup = null!;
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long before = GC.GetTotalMemory(true), allocatedBefore = GC.GetTotalAllocatedBytes(true);
        var clock = Stopwatch.StartNew();
        var state = new AnalysisAccumulator(maxTrackedValues: 128);
        state.ObserveRange(records, new ParallelOptions { MaxDegreeOfParallelism = parallelism });
        var result = state.Complete();
        clock.Stop();
        long allocated = GC.GetTotalAllocatedBytes(true) - allocatedBefore;
        long retained = GC.GetTotalMemory(true) - before;
        using var process = Process.GetCurrentProcess(); process.Refresh();
        var record = new
        {
            Workload = DeterministicRecords.Version, DatasetSha256 = hash, Records = records.Length,
            Cardinality = cardinality, Parallelism = parallelism, TrackedValues = 128,
            Runtime = RuntimeInformation.FrameworkDescription, OS = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(), LogicalProcessors = Environment.ProcessorCount,
            ServerGC = GCSettings.IsServerGC, ElapsedMilliseconds = clock.Elapsed.TotalMilliseconds,
            RecordsPerSecond = records.Length / clock.Elapsed.TotalSeconds, AllocatedBytesAllThreads = allocated,
            RetainedManagedBytesDelta = retained, PeakWorkingSetBytes = process.PeakWorkingSet64,
            ResultRecords = result.TotalObjects
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(record, JsonOptions));
        GC.KeepAlive(records); GC.KeepAlive(state); GC.KeepAlive(result);
        Console.WriteLine(output);
    }
}
