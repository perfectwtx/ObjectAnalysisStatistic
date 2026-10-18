using System.Globalization;
using System.Text.Json;
using ObjectAnalyzer.Data;
using ObjectAnalyzer.TestData;

namespace ObjectAnalyzer.Tests;

public sealed class StatisticalContractTests
{
    [Theory]
    [InlineData(17, 1)]
    [InlineData(17, 4)]
    [InlineData(932, 1)]
    [InlineData(932, 4)]
    [InlineData(20261018, 1)]
    [InlineData(20261018, 4)]
    public void ArbitraryPartitionsAndMergeTrees_PreserveExactStatistics(int seed, int parallelism)
    {
        var random = new Random(seed);
        var records = DeterministicRecords.Create(512, 37).Concat(DeterministicRecords.Create(64, 37)).ToArray();
        var expected = Accumulate(records, 1).Complete();
        var pending = new List<AnalysisAccumulator> { new() };
        for (int offset = 0; offset < records.Length;)
        {
            int count = Math.Min(random.Next(1, 80), records.Length - offset);
            var part = Accumulate(records.Skip(offset).Take(count), parallelism);
            pending.Add(Restore(part));
            offset += count;
        }
        // Random binary merge trees exercise empty identity, associativity, operand order and restoration.
        while (pending.Count > 1)
        {
            int index = random.Next(pending.Count);
            var left = pending[index]; pending.RemoveAt(index);
            index = random.Next(pending.Count);
            var right = pending[index]; pending.RemoveAt(index);
            string untouched = JsonSerializer.Serialize(right.Capture());
            left.Merge(right);
            Assert.Equal(untouched, JsonSerializer.Serialize(right.Capture()));
            pending.Add(Restore(left));
        }
        AssertExact(expected, pending[0].Complete());
    }

    [Fact]
    public void SmallKnownDataset_HasIndependentMathematicalOracle()
    {
        Dictionary<string, object?>[] records = [new() { ["x"] = 1 }, new() { ["x"] = 3 }, new() { ["x"] = 5 },
            new() { ["x"] = null }, new() { ["x"] = "abc" }, new()];
        var result = Accumulate(records, 1).Complete();
        var field = Assert.Single(result.FieldStatistics);
        Assert.Equal(6, result.TotalObjects);
        Assert.Equal(5, field.Count);
        Assert.Equal(1, field.NullCount);
        Assert.Equal(5d / 6, field.Coverage);
        Assert.Equal(9, field.SumNumber);
        Assert.Equal(3, field.AvgNumber);
        Assert.Equal(2, field.StdDevNumber);
        Assert.Equal(3, field.AvgLength);
        Assert.Equal(3, field.TypeCounts["Number"]);
        Assert.Equal(1, field.TypeCounts["String"]);
        Assert.Equal(5, field.ValueCounts.Values.Sum());
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(20000)]
    public void Hll_HasBoundedFixtureErrorAndOrderIndependentMerge(int count)
    {
        var whole = new HyperLogLog(); var left = new HyperLogLog(); var right = new HyperLogLog();
        for (int i = 0; i < count; i++)
        {
            string value = i.ToString(CultureInfo.InvariantCulture);
            whole.Add(value); whole.Add(value);
            (i % 2 == 0 ? left : right).Add(value);
        }
        var a = new HyperLogLog(); a.Merge(left); a.Merge(right);
        var b = new HyperLogLog(); b.Merge(right); b.Merge(left);
        Assert.Equal(whole.Estimate(), a.Estimate());
        Assert.Equal(a.Estimate(), b.Estimate());
        Assert.InRange(Math.Abs(a.Estimate() - count) / (double)count, 0, 0.03);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Quantiles_HaveExplicitRankErrorOnFixedDistributions(int distribution)
    {
        double[] values = Enumerable.Range(0, 6000).Select(i => distribution switch
        { 0 => (double)i, 1 => (double)(i % 13), _ => i < 5700 ? i / 100d : 10000d + i }).Order().ToArray();
        var whole = new MergeableQuantileSketch(); var pieces = Enumerable.Range(0, 7).Select(_ => new MergeableQuantileSketch()).ToArray();
        for (int i = 0; i < values.Length; i++) { whole.Add(values[i]); pieces[i % pieces.Length].Add(values[i]); }
        var merged = new MergeableQuantileSketch(); foreach (var part in pieces.Reverse()) merged.Merge(part);
        Assert.Equal(values.Length, merged.TotalWeight);
        foreach (double q in new[] { 0.5, 0.9, 0.95, 0.99 })
            foreach (var sketch in new[] { whole, merged })
            {
                double estimate = sketch.Quantile(q)!.Value;
                // For ties, q may land anywhere in the equal-value interval. Measure distance to that interval.
                double below = values.Count(value => value < estimate) / (double)values.Length;
                double through = values.Count(value => value <= estimate) / (double)values.Length;
                double error = Math.Max(0, Math.Max(below - q, q - through));
                // Interpolation across discrete atoms can span one entire probability mass (1/13 here).
                double tolerance = distribution == 1 ? 1d / 13 : 0.025;
                if (distribution == 1) Assert.InRange(Math.Abs(estimate - values[(int)Math.Ceiling(q * values.Length) - 1]), 0, 1);
                Assert.True(error <= tolerance, $"distribution={distribution}, q={q}, estimate={estimate}, rank error={error}");
            }
    }

    internal static AnalysisAccumulator Accumulate(IEnumerable<Dictionary<string, object?>> records, int parallelism)
    {
        var state = new AnalysisAccumulator();
        state.ObserveRange(records, new ParallelOptions { MaxDegreeOfParallelism = parallelism });
        return state;
    }
    private static AnalysisAccumulator Restore(AnalysisAccumulator state)
        => JsonSerializer.Deserialize<AnalysisStateSnapshot>(JsonSerializer.Serialize(state.Capture()))!.Restore();
    internal static void AssertExact(AnalysisResult expected, AnalysisResult actual)
    {
        Assert.Equal(expected.TotalObjects, actual.TotalObjects);
        Assert.Equal(expected.TotalUniqueObjects, actual.TotalUniqueObjects);
        Assert.Equal(expected.TotalDuplicateGroups, actual.TotalDuplicateGroups);
        Assert.Equal(expected.FieldStatistics.Select(f => f.FieldName).Order(), actual.FieldStatistics.Select(f => f.FieldName).Order());
        foreach (var field in expected.FieldStatistics)
        {
            var output = actual.FieldStatistics.Single(item => item.FieldName == field.FieldName);
            Assert.Equal(field.Count, output.Count); Assert.Equal(field.NullCount, output.NullCount);
            Assert.Equal(field.Coverage, output.Coverage);
            Assert.Equal(field.TypeCounts.OrderBy(p => p.Key), output.TypeCounts.OrderBy(p => p.Key));
            Assert.Equal(field.ValueCounts.OrderBy(p => p.Key), output.ValueCounts.OrderBy(p => p.Key));
            Assert.Equal(field.OtherValueCount, output.OtherValueCount);
            Assert.Equal(field.MinNumber, output.MinNumber); Assert.Equal(field.MaxNumber, output.MaxNumber);
            if (field.SumNumber is { } sum) Assert.Equal(sum, output.SumNumber!.Value, 8);
            if (field.AvgNumber is { } mean) Assert.Equal(mean, output.AvgNumber!.Value, 10);
            if (field.StdDevNumber is { } deviation) Assert.Equal(deviation, output.StdDevNumber!.Value, 8);
            Assert.Equal(field.MinLength, output.MinLength); Assert.Equal(field.MaxLength, output.MaxLength);
            Assert.Equal(field.AvgLength, output.AvgLength);
        }
    }
}
