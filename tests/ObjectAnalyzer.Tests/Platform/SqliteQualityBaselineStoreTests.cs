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
            TryDelete(databasePath);
            TryDelete(databasePath + "-shm");
            TryDelete(databasePath + "-wal");
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
            TryDelete(databasePath);
            TryDelete(databasePath + "-shm");
            TryDelete(databasePath + "-wal");
        }
    }

    private static void TryDelete(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }
}
