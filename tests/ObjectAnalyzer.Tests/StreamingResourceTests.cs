using System.Text;
using System.Text.Json;
using ObjectAnalyzer.Data;
using ObjectAnalyzer.Analysis.Options;
using ObjectAnalyzer.Analysis.Pipeline;

namespace ObjectAnalyzer.Tests;

public sealed class StreamingResourceTests
{
    private static Dictionary<string, object?> Row(object? value) => new() { ["x"] = value };
    private static SourceRecord Record(int value) => SourceRecord.FromValue(RecordValue.FromObject(Row(value)), new("test"));
    private static AnalysisResourceLimitException Limit(Action action)
    {
        var failure = Assert.ThrowsAny<Exception>(action);
        return failure is AggregateException aggregate
            ? Assert.IsType<AnalysisResourceLimitException>(aggregate.Flatten().InnerExceptions.First())
            : Assert.IsType<AnalysisResourceLimitException>(failure);
    }
    private static AnalysisRunContext Context(string text, string format = "jsonl", MemoryBudget? budget = null) => new()
    { Source = DataSources.Stream(new MemoryStream(Encoding.UTF8.GetBytes(text)), format), MemoryBudget = budget };

    [Fact]
    public void HighCardinality_CountsCoverAllRecordsWhileFrequenciesAreBounded()
    {
        var state = new AnalysisAccumulator(maxTrackedValues: 4);
        state.ObserveRange(Enumerable.Range(0, 2000).Select(value => Row(value)));
        var result = state.Complete(); var field = Assert.Single(result.FieldStatistics);
        Assert.Equal(2000, result.TotalObjects);
        Assert.Equal(2000, result.TotalUniqueObjects);
        Assert.Equal(4, field.ValueCounts.Count);
        Assert.Equal(2000, field.ValueCounts.Values.Sum() + field.OtherValueCount);
        Assert.Contains(result.Reliability!.Limitations, item => item.Metric == "field-frequencies" && item.Accuracy == AnalysisAccuracy.Truncated);
        Assert.Contains(result.Reliability.Limitations, item => item.Metric == "field-distinct");
    }

    [Fact]
    public void ExactObjectBudget_FailsAndPreventsPublishingPartialState()
    {
        var state = new AnalysisAccumulator(resourceLimits: new() { MaxObjectIdentities = 2 });
        Assert.Equal("object-identities", Limit(() => state.ObserveRange(Enumerable.Range(0, 3).Select(value => Row(value)))).Resource);
        Assert.Throws<InvalidOperationException>(() => state.Complete());
        Assert.Throws<InvalidOperationException>(() => state.Capture());
    }

    [Fact]
    public void FieldAdmission_IsSharedAcrossBatchesAndWorkers()
    {
        var state = new AnalysisAccumulator(resourceLimits: new() { MaxFields = 2, BatchSize = 1 });
        Assert.Equal("fields", Limit(() => state.ObserveRange(Enumerable.Range(0, 3).Select(value => new Dictionary<string, object?> { ["field" + value] = 1 }))).Resource);
    }

    [Fact]
    public void RetainedPayloadBudget_FailsEvenWithHeapMonitoringDisabled()
    {
        var state = new AnalysisAccumulator(resourceLimits: new() { MaxRetainedPayloadBytes = 100 });
        Assert.Equal("retained-payload-bytes", Limit(() => state.ObserveRange([Row("hello")])).Resource);
    }

    [Fact]
    public void LargeValues_AreOmittedWithoutChangingViolationCounts()
    {
        var rules = new AnalysisRules { Fields = { ["x"] = new FieldRule { Pattern = "^ok$" } } };
        var state = new AnalysisAccumulator(rules, resourceLimits: new() { MaxFrequencyValueBytes = 128, MaxSampleValueBytes = 128 });
        state.ObserveRange(Enumerable.Range(0, 10).Select(_ => Row(new string('z', 1000))));
        var result = state.Complete(); var field = Assert.Single(result.FieldStatistics);
        Assert.Empty(field.ValueCounts); Assert.Equal(10, field.OtherValueCount);
        var violation = Assert.Single(result.QualityViolations, item => item.Check == "pattern");
        Assert.Equal(10, violation.Count); Assert.Empty(violation.Samples);
        Assert.Contains(result.Reliability!.Limitations, item => item.Metric == "rule-examples");
        var restored = state.Capture().Restore().Complete();
        Assert.Equal(10, Assert.Single(restored.QualityViolations, item => item.Check == "pattern").Count);
    }

    [Fact]
    public void DuplicateExamples_AreBoundedButGroupTotalsAreComplete()
    {
        var state = new AnalysisAccumulator(resourceLimits: new() { MaxObjectSamples = 2 });
        state.ObserveRange(Enumerable.Range(0, 12).SelectMany(value => new[] { Row(value), Row(value) }));
        var result = state.Complete();
        Assert.Equal(12, result.TotalDuplicateGroups); Assert.Equal(12, result.TotalUniqueObjects);
        Assert.Equal(2, result.DuplicateObjects.Count);
        Assert.Contains(result.Reliability!.Limitations, item => item.Metric == "duplicate-examples");
    }

    [Fact]
    public void CorrelationReservoir_KeepsWholeRowsAfterReplacementAndMerge()
    {
        var first = new FieldCorrelationCollector(40, seed: 11);
        var second = new FieldCorrelationCollector(40, seed: 29);
        for (int value = 0; value < 2000; value++)
            (value < 1000 ? first : second).RecordObject(new Dictionary<string, double?> { ["a"] = value, ["b"] = value * 3 });
        first.MergeFrom(second);
        var samples = first.Samples;
        Assert.Equal(2000, first.Seen); Assert.Equal(40, first.SampleCount);
        for (int i = 0; i < 40; i++) Assert.Equal(samples["a"][i] * 3, samples["b"][i]);
        var matrix = FieldCorrelationCalculator.Compute(first);
        Assert.Equal(40, matrix.Samples); Assert.Equal(1d, matrix.Pearson[0][1]!.Value, 10);
    }

    [Fact]
    public void Correlation_LateFieldsNeverPadToPopulationSize()
    {
        var collector = new FieldCorrelationCollector(8, seed: 4);
        for (int i = 0; i < 100_000; i++) collector.RecordObject(new Dictionary<string, double?> { ["a"] = i });
        collector.RecordObject(new Dictionary<string, double?> { ["b"] = 1 });
        Assert.All(collector.Samples.Values, column => Assert.Equal(8, column.Count));
        var limited = new FieldCorrelationCollector(8, maxFields: 1);
        limited.RecordObject(new Dictionary<string, double?> { ["a"] = 1 });
        Assert.Throws<AnalysisResourceLimitException>(() => limited.RecordObject(new Dictionary<string, double?> { ["b"] = 2 }));
    }

    [Fact]
    public void PatternTemplatesAndExamples_HaveIndependentLimits()
    {
        var profiler = new StringPatternProfiler(new() { MaxPatternTemplates = 1, MaxExampleCharacters = 2 });
        profiler.Add("abc"); profiler.Add("def");
        Assert.True(profiler.ExamplesTruncated);
        Assert.Equal(2, profiler.Build().TotalSamples);
        Assert.All(Assert.Single(profiler.Build().Patterns).Examples, sample => Assert.True(sample.Length <= 2));
        Assert.Throws<AnalysisResourceLimitException>(() => profiler.Add("123"));
    }

    [Fact]
    public void TimeHistogramLimit_IsEnforcedAcrossWorkerMerges()
    {
        var state = new AnalysisAccumulator(resourceLimits: new() { MaxTimeBins = 1, BatchSize = 1 });
        Assert.Equal("time-bins", Limit(() => state.ObserveRange([Row(DateTimeOffset.UnixEpoch), Row(DateTimeOffset.UnixEpoch.AddMinutes(1))])).Resource);
    }

    [Fact]
    public void Snapshot_PersistsLimitsAndRechecksThemOnRestore()
    {
        var state = new AnalysisAccumulator(resourceLimits: new() { MaxObjectIdentities = 3 });
        state.ObserveRange([Row(1), Row(2)]);
        var snapshot = JsonSerializer.Deserialize<AnalysisStateSnapshot>(JsonSerializer.Serialize(state.Capture()))!;
        Assert.Equal("analysis-state/v3", snapshot.Version);
        var restored = snapshot.Restore(); restored.ObserveRange([Row(3)]);
        Assert.Equal(3, restored.Complete().TotalObjects);
        snapshot.ResourceLimits = snapshot.ResourceLimits with { MaxObjectIdentities = 1 };
        Assert.Throws<AnalysisResourceLimitException>(() => snapshot.Restore());
    }

    [Fact]
    public void FailedMerge_CannotPublishPartialResult()
    {
        var limits = new AnalysisResourceLimits { MaxObjectIdentities = 1 };
        var first = new AnalysisAccumulator(resourceLimits: limits); first.ObserveRange([Row(1)]);
        var second = new AnalysisAccumulator(resourceLimits: limits); second.ObserveRange([Row(2)]);
        Assert.Throws<AnalysisResourceLimitException>(() => first.Merge(second));
        Assert.Throws<InvalidOperationException>(() => first.Complete());
        Assert.Equal(1, second.Complete().TotalObjects);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OversizedJsonLine_FailsInBothReadModesEvenWithSkipPolicy(bool asynchronous)
    {
        var options = new AnalysisOptions { Parse = { MalformedRecords = MalformedRecordPolicy.Skip }, Memory = { Resources = new() { MaxRecordCharacters = 32 } } };
        var context = Context("{\"x\":\"" + new string('x', 2000) + "\"}\n");
        var executor = new AnalysisExecutor();
        if (asynchronous) await Assert.ThrowsAsync<AnalysisResourceLimitException>(() => executor.ExecuteAsync(options, runContext: context));
        else Assert.Throws<AnalysisResourceLimitException>(() => executor.Execute(options, runContext: context));
    }

    [Fact]
    public void BufferedJsonInput_IsLimitedAndReleased()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("[{\"x\":\"" + new string('z', 4000) + "\"}]"));
        var source = DataSources.Stream(stream, "json", leaveOpen: true);
        var options = new SourceReadOptions { ResourceLimits = new() { MaxBufferedInputBytes = 128 } };
        Assert.Throws<AnalysisResourceLimitException>(() => source.Read(options).ToList());
        Assert.True(stream.CanRead);
    }

    [Fact]
    public void JsonLines_CrlfAndFinalRecordHaveCorrectFraming()
    {
        var result = new AnalysisExecutor().Execute(new AnalysisOptions(), runContext: Context("{\"x\":1}\r\n\r\n{\"x\":2}\r{\"x\":3}"));
        Assert.Equal(3, result.Result.TotalObjects);
    }

    [Fact]
    public void FuzzyAndDistribution_ConsumeTheInputOnceWithoutEarlyMaterialization()
    {
        var options = new AnalysisOptions
        {
            Advanced = { FuzzyDedupField = "x" }, Distribution = { DistributionSnapshotPath = "unused.json" },
            Memory = { Resources = new() { BatchSize = 2 } }
        };
        var source = new ObservedSource();
        var context = new AnalysisRunContext { Source = source, ProgressBridge = new InlineProgress(count => { if (count > 0) source.ProgressSeen = true; }) };
        var result = new AnalysisExecutor().Execute(options, runContext: context);
        Assert.Equal(1, source.Enumerations); Assert.Equal(30, result.Result.TotalObjects);
        Assert.Equal(30, result.Result.FuzzyDuplicates!.SimilarDuplicates);
        Assert.Equal(30, Assert.Single(result.Result.FuzzyDuplicates.Groups).Count);
        Assert.NotNull(result.QuantileSketches);
        Assert.Contains(result.ExecutionProfile!.Capabilities, item => item.Capability == "fuzzy-duplicates" && item.Mode == AnalysisAccessMode.BoundedState);
    }

    [Fact]
    public void PreviewAndSamplingPayloads_AreSeparatelyBounded()
    {
        var options = new AnalysisOptions { Memory = { Resources = new() { MaxPreviewRecords = 2, MaxPreviewBytes = 1000 } } };
        IReadOnlyList<Dictionary<string, object?>>? preview = null;
        new AnalysisExecutor().Execute(options, runContext: new AnalysisRunContext
        { Source = new ObservedSource(), PreviewLimit = 100_000, PreviewSink = rows => preview = rows });
        Assert.Equal(2, preview!.Count);
        options.Sampling.SampleReservoir = 10;
        options.Memory.Resources = options.Memory.Resources with { MaxSamplingBytes = 100 };
        Assert.Equal("sampling-bytes", Limit(() => new AnalysisExecutor().Execute(options, runContext: new() { Source = new ObservedSource() })).Resource);
    }

    [Fact]
    public void SoftMode_SelectsSamplingBeforeAnyPressureAndPreservesCallerOptions()
    {
        var options = new AnalysisOptions { Memory = { MaxMemoryMB = 100, MaxMemoryMode = MaxMemoryMode.Soft }, Sampling = { MemoryFallbackSampleSize = 3 } };
        var result = new AnalysisExecutor().Execute(options, runContext: new()
        { Source = DataSources.Records(Enumerable.Range(0, 10).Select(value => RecordValue.FromObject(Row(value)))), MemoryBudget = new(100, MaxMemoryMode.Soft, () => 0) });
        Assert.Equal(3, result.Result.TotalObjects); Assert.NotNull(result.Result.SamplingConfidence);
        Assert.Equal(0, options.Sampling.SampleReservoir);
        Assert.Null(result.Accumulator);
    }

    [Fact]
    public void HardHeapPressureDuringBufferedParsing_IsObservedBeforeCompletion()
    {
        int reads = 0;
        var context = Context("[{\"x\":1}]", "json", new MemoryBudget(1, MaxMemoryMode.Hard, () => ++reads < 4 ? 0 : 2 * 1024 * 1024));
        Assert.Throws<MaxMemoryExceededException>(() => new AnalysisExecutor().Execute(new AnalysisOptions(), runContext: context));
    }

    [Fact]
    public void ExecutionPlan_DescribesSourceBufferingWithoutOpeningIt()
    {
        var plan = new AnalysisExecutionPlan(new AnalysisOptions());
        var profile = plan.Describe(DataSources.Stream(new MemoryStream(), "json").Descriptor);
        Assert.Contains(profile.Capabilities, item => item.Capability == "input" && item.Mode == AnalysisAccessMode.BufferedInput);
        Assert.Equal(1024, profile.BatchSize);
    }

    [Fact]
    public void StreamingInput_IsNotLimitedByTheBufferedDocumentQuota()
    {
        var options = new AnalysisOptions { Memory = { Resources = new() { MaxBufferedInputBytes = 8 } } };
        var result = new AnalysisExecutor().Execute(options, runContext: Context(string.Concat(Enumerable.Repeat("{\"x\":1}\n", 2000))));
        Assert.Equal(2000, result.Result.TotalObjects);
        Assert.Contains(result.ExecutionProfile!.Capabilities, item => item.Capability == "input" && item.Mode == AnalysisAccessMode.SinglePass);
    }

    [Fact]
    public void Restore_RechecksPayloadAndTimeBinQuotas()
    {
        var state = new AnalysisAccumulator();
        state.ObserveRange([Row(DateTimeOffset.UnixEpoch), Row(DateTimeOffset.UnixEpoch.AddMinutes(1))]);
        var snapshot = state.Capture();
        snapshot.ResourceLimits = snapshot.ResourceLimits with { MaxRetainedPayloadBytes = 1 };
        Assert.Throws<AnalysisResourceLimitException>(() => snapshot.Restore());
        snapshot.ResourceLimits = new() { MaxTimeBins = 1 };
        Assert.Throws<InvalidDataException>(() => snapshot.Restore());
    }

    [Fact]
    public void PlanLimits_AreFrozenAndCompiledRulesAreCountedExactly()
    {
        var options = new AnalysisOptions { Memory = { Resources = new() { MaxObjectIdentities = 1 } } };
        var plan = new AnalysisExecutionPlan(options);
        options.Memory.Resources = new();
        Assert.Equal("object-identities", Limit(() => new AnalysisExecutor().Execute(plan, runContext: Context("{\"x\":1}\n{\"x\":2}"))).Resource);
        var rules = new AnalysisRules { Fields = { ["x"] = new FieldRule { Pattern = "^a$" } } };
        Assert.Throws<AnalysisResourceLimitException>(() => new AnalysisAccumulator(rules, resourceLimits: new() { MaxRulesPerField = 0 }));
        var emptyRules = new AnalysisRules { Fields = { ["x"] = new FieldRule() } };
        _ = new AnalysisAccumulator(emptyRules, resourceLimits: new() { MaxRulesPerField = 0 });
    }

    [Theory]
    [InlineData(".json")]
    [InlineData(".md")]
    [InlineData(".html")]
    [InlineData(".csv")]
    public void ExportedAndRedactedResults_PreserveReliability(string extension)
    {
        string path = Path.Combine(Path.GetTempPath(), "oa-resource-" + Guid.NewGuid().ToString("N") + extension);
        try
        {
            var state = new AnalysisAccumulator(maxTrackedValues: 1);
            state.ObserveRange([Row(1), Row(2)]);
            var result = state.Complete();
            result.FieldStatistics[0].SemanticType = "Email";
            var redacted = PiiRedactor.Redact(result);
            Assert.NotSame(result, redacted);
            Assert.NotNull(redacted.Reliability);
            Assert.NotSame(result.Reliability, DataQualityReport.Build(redacted).Reliability);
            Assert.Equal(result.Reliability!.Summary, redacted.Reliability!.Summary);
            ReportExporter.Save(redacted, path);
            Assert.Contains("field-frequencies", File.ReadAllText(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private sealed class InlineProgress(Action<long> action) : IProgress<long> { public void Report(long value) => action(value); }
    private sealed class ObservedSource : IAnalysisDataSource
    {
        public int Enumerations; public bool ProgressSeen;
        public SourceDescriptor Descriptor { get; } = new("test", "records", new(true, false, false, false, false, false, SourcePositionKind.Record));
        public IEnumerable<SourceRecord> Read(SourceReadOptions options, CancellationToken cancellationToken = default)
        {
            Assert.Equal(1, ++Enumerations);
            for (int i = 0; i < 30; i++)
            {
                if (i == 10 && options.ResourceLimits.BatchSize == 2) Assert.True(ProgressSeen);
                yield return Record(1);
            }
        }
    }
}
