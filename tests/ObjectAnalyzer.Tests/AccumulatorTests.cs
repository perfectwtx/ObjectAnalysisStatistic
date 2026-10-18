using System.Text.Json;
using ObjectAnalyzer.Analysis.Options;
using ObjectAnalyzer.Data;

namespace ObjectAnalyzer.Tests;

public class AccumulatorTests
{
    private static Dictionary<string, object?> Row(object? value) => new() { ["x"] = value };

    private static AnalysisAccumulator Accumulate(IEnumerable<Dictionary<string, object?>> rows, int limit = 1000, int parallelism = 1)
    {
        var state = new AnalysisAccumulator(maxTrackedValues: limit);
        state.ObserveRange(rows, new ParallelOptions { MaxDegreeOfParallelism = parallelism });
        return state;
    }

    [Fact]
    public void Merge_UsesEffectiveNumericAndStringCounts_AndSingletonVariance()
    {
        var a = Accumulate([Row(1), Row(null), Row("abc"), new()]);
        var b = Accumulate([Row(3), Row("1234567"), Row(null)]);
        a.Merge(b);
        var field = Assert.Single(a.Complete().FieldStatistics);
        Assert.Equal(6, field.Count);
        Assert.Equal(2, field.NullCount);
        Assert.Equal(6d / 7, field.Coverage);
        Assert.Equal(2, field.AvgNumber);
        Assert.Equal(Math.Sqrt(2), field.StdDevNumber!.Value, 12);
        Assert.Equal(5, field.AvgLength);
        Assert.Equal(2, a.Capture().Fields[0].Metrics.NumberCount);
        Assert.Equal(2, a.Capture().Fields[0].Metrics.StringCount);
    }

    [Fact]
    public void Merge_DetectsDuplicatesAcrossBatches_AndMergesQuantiles()
    {
        var rows = Enumerable.Range(0, 100).Select(i => Row(i)).Concat([Row(42), Row(99)]).ToList();
        var merged = Accumulate(rows.Take(50));
        var other = Accumulate(rows.Skip(50));
        merged.Merge(other);
        var expected = Accumulate(rows).Complete();
        var actual = merged.Complete();
        Assert.Equal(100, actual.TotalUniqueObjects);
        Assert.Equal(2, actual.TotalDuplicateGroups);
        Assert.Equal(expected.TotalObjects, actual.TotalObjects);
        Assert.Equal(2, actual.DuplicateObjects.Count);
        Assert.All(actual.DuplicateObjects, d => Assert.Equal(2, d.Count));
        var field = Assert.Single(actual.FieldStatistics);
        var expectedField = Assert.Single(expected.FieldStatistics);
        Assert.Equal(expectedField.MedianNumber, field.MedianNumber);
        Assert.Equal(expectedField.P99Number, field.P99Number);
        Assert.Equal(expectedField.StdDevNumber!.Value, field.StdDevNumber!.Value, 10);
        Assert.Equal(52, other.TotalObjects);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(1000)]
    public void Truncation_IsIndependentOfPartitionOrder_AndPreservesOccurrenceCounts(int limit)
    {
        var rows = Enumerable.Range(0, 2500).Select(i => Row(i % 1200)).ToList();
        var expected = Assert.Single(Accumulate(rows, limit).Complete().FieldStatistics);
        var a = Accumulate(rows.Take(800), limit);
        var b = Accumulate(rows.Skip(800).Take(800), limit);
        var c = Accumulate(rows.Skip(1600), limit);
        c.Merge(a);
        c.Merge(b);
        var actual = Assert.Single(c.Complete().FieldStatistics);
        Assert.Equal(expected.ValueCounts.OrderBy(p => p.Key), actual.ValueCounts.OrderBy(p => p.Key));
        Assert.Equal(expected.OtherValueCount, actual.OtherValueCount);
        Assert.Equal(rows.Count, actual.ValueCounts.Values.Sum() + actual.OtherValueCount);
        Assert.Equal(expected.DistinctCount, actual.DistinctCount);
    }

    [Fact]
    public void Snapshot_RoundTripsMomentsDatesHistogramsAndSketches_ThenContinues()
    {
        var rows = Enumerable.Range(0, 1800).Select(i => new Dictionary<string, object?>
        {
            ["x"] = i, ["date"] = $"2026-10-{i % 28 + 1:00}", ["text"] = i % 2 == 0 ? "abc" : "abcde"
        }).ToList();
        var first = Accumulate(rows.Take(900), 20);
        string json = JsonSerializer.Serialize(first.Capture());
        var restored = JsonSerializer.Deserialize<AnalysisStateSnapshot>(json)!.Restore();
        restored.ObserveRange(rows.Skip(900));
        var expected = Accumulate(rows, 20).Complete();
        var actual = restored.Complete();
        foreach (var field in expected.FieldStatistics)
        {
            var output = actual.FieldStatistics.Single(f => f.FieldName == field.FieldName);
            Assert.Equal(field.Count, output.Count);
            Assert.Equal(field.AvgLength, output.AvgLength);
            Assert.Equal(field.MinDateTime, output.MinDateTime);
            Assert.Equal(field.MaxDateTime, output.MaxDateTime);
            Assert.Equal(field.DateTimeCount, output.DateTimeCount);
            Assert.Equal(field.DistinctCount, output.DistinctCount);
            Assert.Equal(field.Histogram?.Sum(h => h.Count), output.Histogram?.Sum(h => h.Count));
            if (field.AvgNumber.HasValue)
            {
                Assert.Equal(field.AvgNumber.Value, output.AvgNumber!.Value, 10);
                Assert.Equal(field.StdDevNumber!.Value, output.StdDevNumber!.Value, 8);
                Assert.InRange(Math.Abs(field.MedianNumber!.Value - output.MedianNumber!.Value), 0, 15);
            }
        }
    }

    [Fact]
    public void CompleteAndCapture_AreDetachedFromMutableState()
    {
        var state = Accumulate([Row(1), Row(3)]);
        var report = state.Complete();
        var snapshot = state.Capture();
        report.FieldStatistics[0].ValueCounts.Clear();
        report.FieldStatistics[0].TypeCounts.Clear();
        snapshot.Fields[0].Counters.ValueCounts.Clear();
        state.ObserveRange([Row(5)]);
        Assert.Equal(3, Assert.Single(state.Complete().FieldStatistics).ValueCounts.Count);
        Assert.Equal(2, report.TotalObjects);
        Assert.Equal(2, snapshot.TotalObjects);
        Assert.Equal(3, state.TotalObjects);
    }

    [Fact]
    public void EmptyMerge_IsIdentity_AndMismatchedStateIsRejected()
    {
        var a = Accumulate([Row(1)]);
        a.Merge(new AnalysisAccumulator());
        Assert.Equal(1, a.TotalObjects);
        Assert.Throws<ArgumentException>(() => a.Merge(a));
        Assert.Throws<InvalidOperationException>(() => a.Merge(new AnalysisAccumulator(maxTrackedValues: 5)));
        Assert.Throws<NotSupportedException>(() => new AnalysisAccumulator(features: new AdvancedAnalysisOptions { EnableCorrelation = true }).Capture());
        Assert.Throws<InvalidDataException>(() => new AnalysisStateSnapshot { Version = "old" }.Restore());
        Assert.Throws<InvalidDataException>(() => new AnalysisStateSnapshot { TotalObjects = 1 }.Restore());
    }

    [Fact]
    public void ParallelAndSequentialAccumulation_Agree()
    {
        var rows = Enumerable.Range(0, 12000).Select(i => Row(i % 2000)).ToList();
        var a = Accumulate(rows, 30).Complete();
        var b = Accumulate(rows, 30, 4).Complete();
        Assert.Equal(a.TotalUniqueObjects, b.TotalUniqueObjects);
        Assert.Equal(a.TotalDuplicateGroups, b.TotalDuplicateGroups);
        var left = Assert.Single(a.FieldStatistics);
        var right = Assert.Single(b.FieldStatistics);
        Assert.Equal(left.ValueCounts.OrderBy(p => p.Key), right.ValueCounts.OrderBy(p => p.Key));
        Assert.Equal(left.OtherValueCount, right.OtherValueCount);
        Assert.Equal(left.AvgNumber!.Value, right.AvgNumber!.Value, 8);
        Assert.Equal(left.StdDevNumber!.Value, right.StdDevNumber!.Value, 8);
    }

    [Fact]
    public void TypedObserve_UsesTheSameStateProtocol()
    {
        var state = new AnalysisAccumulator();
        state.Observe(RecordNormalizer.Normalize(RecordValue.FromObject(new Dictionary<string, object?> { ["a.b"] = 1 })));
        Assert.Equal("[\"a.b\"]", Assert.Single(state.Complete().FieldStatistics).FieldName);
    }

    [Fact]
    public void Complete_ReevaluatesRulesAfterMerge_AndDoesNotShareRuleConfiguration()
    {
        var rules = new AnalysisRules { Fields = { ["x"] = new FieldRule { Unique = true } } };
        var a = new AnalysisAccumulator(rules);
        var b = new AnalysisAccumulator(rules);
        a.ObserveRange([Row(1)]);
        b.ObserveRange([Row(1)]);
        Assert.Empty(a.Complete().QualityViolations);
        a.Merge(b);
        var report = a.Complete();
        Assert.Equal(1, Assert.Single(report.QualityViolations).Count);
        report.FieldStatistics[0].AppliedRule!.Unique = false;
        rules.Fields["x"].Unique = false;
        Assert.Equal(1, Assert.Single(a.Complete().QualityViolations).Count);
    }
}
