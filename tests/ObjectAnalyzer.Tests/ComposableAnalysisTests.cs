using System.Text.Json;
using ObjectAnalyzer.Data;

namespace ObjectAnalyzer.Tests;

public class ComposableAnalysisTests
{
    private static Dictionary<string, object?> Row(object? value) => new() { ["x"] = value };

    [Fact]
    public void RegisteredAnalyzer_ObservesOnlySupportedTypes_AndSurvivesMergeAndCheckpoint()
    {
        var factory = new CounterAnalyzer();
        var registry = FieldAnalyzerRegistry.Default.With(factory);
        var strings = new AnalysisAccumulator(analyzers: registry);
        strings.ObserveRange([Row("text")]);
        Assert.Equal(0, factory.Created);
        Assert.Null(Assert.Single(strings.Complete().FieldStatistics).AnalyzerResults);

        var a = new AnalysisAccumulator(analyzers: registry);
        var b = new AnalysisAccumulator(analyzers: registry);
        a.ObserveRange([Row(1), Row(null)]);
        b.ObserveRange([Row(2), Row(3)]);
        a.Merge(b);
        Assert.Equal(3, Assert.Single(a.Complete().FieldStatistics).AnalyzerResults![factory.Id].GetInt64());
        string path = Path.Combine(Path.GetTempPath(), $"oa-components-{Guid.NewGuid():N}.snapshot");
        try
        {
            new AnalyzerSnapshot { State = a.Capture(), PositionKind = "batch" }.SaveTo(path, registry);
            Assert.Throws<InvalidDataException>(() => AnalyzerSnapshot.LoadFrom(path));
            var loaded = AnalyzerSnapshot.LoadFrom(path, registry);
            Assert.Equal(3, loaded.Result.FieldStatistics[0].AnalyzerResults![factory.Id].GetInt64());
            var restored = loaded.State.Restore(registry);
            restored.ObserveRange([Row(4)]);
            Assert.Equal(4, Assert.Single(restored.Complete().FieldStatistics).AnalyzerResults![factory.Id].GetInt64());
            Assert.Equal(2, Assert.Single(b.Complete().FieldStatistics).AnalyzerResults![factory.Id].GetInt64());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void DisabledAnalyzer_DoesNotAllocateStateOrProduceItsMetrics()
    {
        var registry = FieldAnalyzerRegistry.Default.Without("numeric").Without("date");
        var state = new AnalysisAccumulator(analyzers: registry);
        state.ObserveRange([Row(1), Row(3), Row("2026-10-18")]);
        var field = Assert.Single(state.Complete().FieldStatistics);
        Assert.Null(field.AvgNumber);
        Assert.Null(field.DateTimeCount);
        Assert.Equal(3, field.Count);
        Assert.Equal(3, field.DistinctCount);
        var captured = Assert.Single(state.Capture().Fields);
        Assert.DoesNotContain(captured.Components, c => c.Id is "numeric" or "date");
        var restored = state.Capture().Restore();
        Assert.Null(Assert.Single(restored.Complete().FieldStatistics).AvgNumber);
    }

    [Fact]
    public void CustomRule_IsCompiledOncePerField_AndCountsAllOccurrencesWithoutFrequencyStorage()
    {
        var compiler = new EvenCompiler();
        var registry = FieldAnalyzerRegistry.Default.WithRule(compiler);
        var rules = new AnalysisRules { Fields = { ["x"] = new FieldRule { Checks = new() { [compiler.Id] = JsonSerializer.SerializeToElement(2) } } } };
        var state = new AnalysisAccumulator(rules, maxTrackedValues: 0, analyzers: registry);
        Assert.Equal(1, compiler.Compilations);
        state.ObserveRange(Enumerable.Range(0, 2000).Select(i => Row(i)));
        var report = state.Complete();
        var violation = Assert.Single(report.QualityViolations);
        Assert.Equal(1000, violation.Count);
        Assert.False(violation.Approximate);
        Assert.True(violation.DistinctApproximate);
        Assert.InRange(violation.DistinctValueCount!.Value, 950, 1050);
        Assert.Equal(QualityChecks.MaxSamples, violation.Samples.Count);
        Assert.Empty(Assert.Single(report.FieldStatistics).ValueCounts);
        Assert.Equal(1, compiler.Compilations);
        var restored = JsonSerializer.Deserialize<AnalysisStateSnapshot>(JsonSerializer.Serialize(state.Capture()))!.Restore(registry);
        restored.ObserveRange([Row(2001)]);
        Assert.Equal(1001, Assert.Single(restored.Complete().QualityViolations).Count);
        Assert.Equal(2, compiler.Compilations);
    }

    [Fact]
    public void EnumAndPatternCounts_AreExactAcrossTruncationAndPartitioning()
    {
        var rules = new AnalysisRules { Fields = { ["x"] = new FieldRule
        {
            EnumValues = [JsonSerializer.SerializeToElement("OK")], Pattern = "^OK$"
        } } };
        var a = new AnalysisAccumulator(rules, maxTrackedValues: 0);
        var b = new AnalysisAccumulator(rules, maxTrackedValues: 0);
        a.ObserveRange(Enumerable.Range(0, 1000).Select(_ => Row("BAD")));
        b.ObserveRange([Row("BAD"), Row("OK"), Row(null)]);
        a.Merge(b);
        var report = a.Complete();
        var enums = report.QualityViolations.Single(v => v.Check == QualityCheckTypes.EnumValues);
        var pattern = report.QualityViolations.Single(v => v.Check == QualityCheckTypes.Pattern);
        Assert.Equal(1002, enums.Count);
        Assert.Equal(2, enums.DistinctValueCount);
        Assert.Equal(1001, pattern.Count);
        Assert.False(pattern.Approximate);
        var field = Assert.Single(report.FieldStatistics);
        Assert.Equal(1002, field.EnumViolationCount);
        Assert.Equal(2, field.EnumViolations.Count);
        Assert.Equal(2003, DataQualityReport.Build(report).TotalViolationCount);
        Assert.Equal(1002, a.Capture().Restore().Complete().FieldStatistics[0].EnumViolationCount);
    }

    [Fact]
    public void HighCardinalityEnumSamples_AreBounded_AndTotalsAreExported()
    {
        var rules = new AnalysisRules { Fields = { ["x"] = new FieldRule { EnumValues = [JsonSerializer.SerializeToElement(-1)] } } };
        var state = new AnalysisAccumulator(rules, maxTrackedValues: 1);
        state.ObserveRange(Enumerable.Range(0, 2000).Select(i => Row(i)));
        var result = state.Complete();
        var field = Assert.Single(result.FieldStatistics);
        Assert.Equal(2000, field.EnumViolationCount);
        Assert.Equal(QualityChecks.MaxSamples, field.EnumViolations.Count);
        string path = Path.Combine(Path.GetTempPath(), $"oa-rule-export-{Guid.NewGuid():N}.json");
        try
        {
            ViolationExporter.Save(result, path, redactPii: false);
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(2000, document.RootElement.GetProperty("fieldViolations")[0].GetProperty("count").GetInt64());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void RequiredRule_IsEvaluatedEvenWhenFieldNeverAppears()
    {
        var rules = new AnalysisRules { Fields = { ["missing"] = new FieldRule { Required = true } } };
        var result = Analyze([Row(1), Row(2)], rules);
        var missing = result.FieldStatistics.Single(f => f.FieldName == "missing");
        Assert.Equal(0, missing.Count);
        Assert.Equal(0, missing.Coverage);
        Assert.Equal(2, Assert.Single(result.QualityViolations).Count);
    }

    [Fact]
    public void InvalidRulesAndMissingDependencies_FailBeforeEnumeration()
    {
        bool enumerated = false;
        IEnumerable<Dictionary<string, object?>> Input() { enumerated = true; yield return Row(1); }
        var rules = new AnalysisRules { Fields = { ["x"] = new FieldRule { Pattern = "[" } } };
        Assert.ThrowsAny<ArgumentException>(() => Analyze(Input(), rules));
        Assert.False(enumerated);
        Assert.Throws<ArgumentException>(() => new AnalysisAccumulator(new AnalysisRules
        {
            Fields = { ["x"] = new FieldRule { Unique = true } }
        }, analyzers: FieldAnalyzerRegistry.Default.Without("distinct")));
        Assert.Throws<ArgumentException>(() => new AnalysisAccumulator(new AnalysisRules
        {
            Fields = { ["x"] = new FieldRule { Checks = new() { ["unknown"] = JsonSerializer.SerializeToElement(true) } } }
        }));
        Assert.Throws<InvalidDataException>(() => new AnalysisStateSnapshot { Version = "analysis-state/v1" }.Restore());
    }

    [Fact]
    public void Registrations_AreIsolated_AndConfigurationVersionsAreChecked()
    {
        var factory = new CounterAnalyzer();
        var custom = FieldAnalyzerRegistry.Default.With(factory);
        Assert.Throws<ArgumentException>(() => custom.With(factory));
        Assert.DoesNotContain(FieldAnalyzerRegistry.Default.Descriptors, d => d.Id == factory.Id);
        var state = new AnalysisAccumulator(analyzers: custom);
        state.ObserveRange([Row(1)]);
        Assert.Throws<InvalidOperationException>(() => state.Merge(new AnalysisAccumulator()));
        var changed = FieldAnalyzerRegistry.Default.With(new CounterAnalyzer(version: 2));
        Assert.Throws<InvalidDataException>(() => state.Capture().Restore(changed));
    }

    [Fact]
    public void NewRuleSamples_AreRedactedWithoutMutatingOriginalState()
    {
        var rules = new AnalysisRules { Fields = { ["x"] = new FieldRule { Pattern = "^allowed$", Pii = new PiiRule { Label = "secret" } } } };
        var state = new AnalysisAccumulator(rules, analyzers: FieldAnalyzerRegistry.Default.With(new CounterAnalyzer()));
        state.ObserveRange([Row(123456789)]);
        var original = state.Complete();
        var redacted = PiiRedactor.Redact(original);
        Assert.NotEqual("123456789", Assert.Single(redacted.FieldStatistics[0].ValueRuleResults![QualityCheckTypes.Pattern].Samples));
        Assert.Null(redacted.FieldStatistics[0].AnalyzerResults);
        Assert.Equal("123456789", Assert.Single(state.Complete().FieldStatistics[0].ValueRuleResults![QualityCheckTypes.Pattern].Samples));
    }

    [Fact]
    public void ResultRules_AreRegisteredAndReevaluatedAfterMerge()
    {
        var registry = FieldAnalyzerRegistry.Default.WithResultRule(new MinimumCompiler(3));
        var a = new AnalysisAccumulator(analyzers: registry);
        a.ObserveRange([Row(1), Row(2)]);
        Assert.Equal("test.minimum", Assert.Single(a.Complete().QualityViolations).Check);
        var saved = a.Capture();
        Assert.Throws<InvalidDataException>(() => saved.Restore());
        Assert.Throws<InvalidDataException>(() => saved.Restore(FieldAnalyzerRegistry.Default.WithResultRule(new MinimumCompiler(5))));
        var restored = saved.Restore(registry);
        var b = new AnalysisAccumulator(analyzers: registry);
        b.ObserveRange([Row(3)]);
        restored.Merge(b);
        Assert.Empty(restored.Complete().QualityViolations);
        Assert.Single(a.Complete().QualityViolations);
    }

    [Fact]
    public void RestoringAMissingObservedComponent_IsRejected()
    {
        var rules = new AnalysisRules { Fields = { ["x"] = new FieldRule { Pattern = "^OK$" } } };
        var state = new AnalysisAccumulator(rules);
        state.ObserveRange([Row("BAD")]);
        var saved = state.Capture();
        Assert.Single(saved.Fields).Components.RemoveAll(c => c.Id == "value-rules");
        Assert.Throws<InvalidDataException>(() => saved.Restore());
    }

    [Fact]
    public void ParallelRuleCounters_AndBoundedSamples_AgreeWithSequentialAndRestoredState()
    {
        var rules = new AnalysisRules { Fields = { ["x"] = new FieldRule { Pattern = "^OK$", EnumValues = [JsonSerializer.SerializeToElement("OK")] } } };
        var rows = Enumerable.Range(0, 12000).Select(i => Row(i % 3 == 0 ? "OK" : $"BAD-{i % 700}")).ToList();
        var serial = new AnalysisAccumulator(rules, maxTrackedValues: 0);
        var parallel = new AnalysisAccumulator(rules, maxTrackedValues: 0);
        serial.ObserveRange(rows, new ParallelOptions { MaxDegreeOfParallelism = 1 });
        parallel.ObserveRange(rows, new ParallelOptions { MaxDegreeOfParallelism = 4 });
        var expected = serial.Complete();
        var actual = parallel.Capture().Restore().Complete();
        foreach (var violation in expected.QualityViolations)
        {
            var output = actual.QualityViolations.Single(v => v.Check == violation.Check);
            Assert.Equal(8000, output.Count);
            Assert.Equal(violation.DistinctValueCount, output.DistinctValueCount);
            Assert.Equal(violation.Samples, output.Samples);
            Assert.False(output.Approximate);
        }
    }

    private sealed class CounterAnalyzer(int version = 1) : IFieldAnalyzer
    {
        public string Id => "test.numeric-count";
        public int Version => version;
        public int Created;
        public IReadOnlyCollection<RecordValueKind> InputKinds { get; } = new[] { RecordValueKind.Number };
        public IFieldAnalyzerState CreateState(FieldAnalyzerContext context) { Interlocked.Increment(ref Created); return new CounterState(Id, 0); }
        public IFieldAnalyzerState RestoreState(FieldAnalyzerContext context, JsonElement data) => new CounterState(Id, data.GetInt64());
    }
    private sealed class CounterState(string id, long initial) : IFieldAnalyzerState
    {
        private long _count = initial;
        public void Observe(RecordValue value) => _count++;
        public void Merge(IFieldAnalyzerState other) => _count += ((CounterState)other)._count;
        public void Complete(FieldStatistic result) => (result.AnalyzerResults ??= new())[id] = JsonSerializer.SerializeToElement(_count);
        public JsonElement Capture() => JsonSerializer.SerializeToElement(_count);
    }
    private sealed class EvenCompiler : IValueRuleCompiler
    {
        public string Id => "even";
        public int Compilations;
        public CompiledValueRule? Compile(FieldPath path, FieldRule definition)
        {
            Interlocked.Increment(ref Compilations);
            if (definition.Checks?.TryGetValue(Id, out var config) != true) return null;
            int divisor = config.GetInt32();
            if (divisor <= 0) throw new ArgumentException("Invalid divisor.");
            return new(Id, "必须为指定整数的倍数", v => !v.TryGetDouble(out double n) || n % divisor == 0);
        }
    }
    private sealed class MinimumCompiler(long minimum) : IResultRuleCompiler
    {
        public string Id => "test.minimum";
        public string ConfigurationKey => minimum.ToString(System.Globalization.CultureInfo.InvariantCulture);
        public CompiledResultRule Compile(AnalysisRules? definition) => new(context => context.TotalObjects < minimum
            ? new[] { new FieldViolation { Check = Id, Count = context.TotalObjects, Message = "记录数不足" } }
            : Array.Empty<FieldViolation>());
    }
}
