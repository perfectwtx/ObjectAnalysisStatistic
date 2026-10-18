using ObjectAnalyzer.Platform;

namespace ObjectAnalyzer.Tests.Platform;

public sealed class SqliteQualityBaselineStoreTests
{
    [Fact]
    public async Task SaveGetAndDelete_Works()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"objectanalyzer-baseline-{Guid.NewGuid():N}.db");
        try
        {
            var store = new SqliteQualityBaselineStore(databasePath);
            await store.InitializeAsync();

            var baseline = new QualityBaseline("orders", 91, 92, 93, 94, 95, 96);
            await store.SaveAsync(baseline);

            var loaded = await store.GetAsync("orders");
            Assert.Equal(baseline, loaded);

            var all = await store.GetAllAsync();
            Assert.Single(all);

            await store.DeleteAsync("orders");
            Assert.Null(await store.GetAsync("orders"));
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Save_ExistingSource_UpdatesBaseline()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"objectanalyzer-baseline-{Guid.NewGuid():N}.db");
        try
        {
            var store = new SqliteQualityBaselineStore(databasePath);
            await store.InitializeAsync();
            await store.SaveAsync(new QualityBaseline("orders", 90));
            await store.SaveAsync(new QualityBaseline("orders", 95));

            var loaded = await store.GetAsync("orders");

            Assert.NotNull(loaded);
            Assert.Equal(95, loaded.MinimumOverallScore);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task ParallelTemporaryDatabases_AreReleasedWithoutClearingOtherPools()
    {
        await Task.WhenAll(Enumerable.Range(0, 8).Select(async index =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"oa-baseline-isolation-{Guid.NewGuid():N}.db");
            try
            {
                var store = new SqliteQualityBaselineStore(path);
                await store.InitializeAsync();
                await store.SaveAsync(new QualityBaseline("source-" + index, 90));
                Assert.NotNull(await store.GetAsync("source-" + index));
            }
            finally { DeleteDatabase(path); }
            Assert.False(File.Exists(path));
        }));
    }

    private static void DeleteDatabase(string path) => SqliteTestDatabase.Delete(path);
}
