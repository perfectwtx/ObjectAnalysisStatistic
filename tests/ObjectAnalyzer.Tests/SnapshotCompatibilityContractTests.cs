using System.Text.Json;

namespace ObjectAnalyzer.Tests;

public sealed class SnapshotCompatibilityContractTests
{
    [Fact]
    public void FrozenV3State_RestoresAndContinuesWithoutRewritingFixture()
    {
        var snapshot = JsonSerializer.Deserialize<AnalysisStateSnapshot>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Contracts", "state-v3.json")))!;
        Assert.Equal("analysis-state/v3", snapshot.Version);
        var state = snapshot.Restore();
        state.ObserveRange([new() { ["amount"] = 5, ["label"] = "three" }]);
        var result = state.Complete();
        Assert.Equal(3, result.TotalObjects);
        Assert.Equal(3, result.FieldStatistics.Single(field => field.FieldName == "amount").AvgNumber);
        Assert.Equal(1, result.FieldStatistics.Single(field => field.FieldName == "label").NullCount);
    }

    [Theory]
    [InlineData("analysis-state/v1")]
    [InlineData("analysis-state/v2")]
    [InlineData("analysis-state/v999")]
    public void IncompatibleStateVersions_AreRejectedExplicitly(string version)
        => Assert.Throws<InvalidDataException>(() => new AnalysisStateSnapshot { Version = version }.Restore());

    [Fact]
    public void PersistedAndDeliveryContractIdentifiers_ArePinned()
    {
        Assert.Equal("object-analyzer.snapshot/v3", new AnalyzerSnapshot().Schema);
        Assert.Equal("2.0", new SchemaSnapshot().SnapshotVersion);
        Assert.Equal("2.0", new DistributionSnapshot().SnapshotVersion);
        Assert.Equal("result-delivery/v1", new ResultProjectionInfo().Contract);
        Assert.Equal("schema-model/v1", SchemaInference.Infer(new AnalysisResult()).Version);
    }
}
