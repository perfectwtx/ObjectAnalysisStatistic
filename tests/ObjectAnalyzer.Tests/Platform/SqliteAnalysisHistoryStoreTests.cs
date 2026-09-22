using ObjectAnalyzer.Platform;

namespace ObjectAnalyzer.Tests.Platform;

public sealed class SqliteAnalysisHistoryStoreTests
{
    [Fact]
    public async Task TryTransitionAsync_OnlyOneConcurrentTransitionWins()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"object-analyzer-{Guid.NewGuid():N}.db");
        try
        {
            var store = new SqliteAnalysisHistoryStore(databasePath);
            await store.InitializeAsync();

            var jobId = Guid.NewGuid();
            var job = new AnalysisJob
            {
                Id = jobId,
                SourceName = "test",
                SourceType = "sqlite",
                Status = AnalysisJobStatus.Running
            };
            await store.SaveAsync(job);

            var succeeded = new AnalysisJob
            {
                Id = jobId,
                SourceName = job.SourceName,
                SourceType = job.SourceType,
                Status = AnalysisJobStatus.Succeeded,
                CompletedAtUtc = DateTime.UtcNow,
                ResultJson = "{}"
            };
            var failed = new AnalysisJob
            {
                Id = jobId,
                SourceName = job.SourceName,
                SourceType = job.SourceType,
                Status = AnalysisJobStatus.Failed,
                CompletedAtUtc = DateTime.UtcNow,
                ErrorMessage = "late failure"
            };

            var results = await Task.WhenAll(
                store.TryTransitionAsync(succeeded, AnalysisJobStatus.Running),
                store.TryTransitionAsync(failed, AnalysisJobStatus.Running));

            Assert.Equal(1, results.Count(x => x));
            var persisted = await store.GetAsync(jobId);
            Assert.NotNull(persisted);
            Assert.Contains(
                persisted!.Status,
                new[] { AnalysisJobStatus.Succeeded, AnalysisJobStatus.Failed });
        }
        finally
        {
            TryDelete(databasePath);
        }
    }

    [Fact]
    public async Task TryTransitionAsync_DoesNotOverwriteTerminalState()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"object-analyzer-{Guid.NewGuid():N}.db");
        try
        {
            var store = new SqliteAnalysisHistoryStore(databasePath);
            await store.InitializeAsync();

            var job = new AnalysisJob
            {
                SourceName = "test",
                SourceType = "sqlite",
                Status = AnalysisJobStatus.Running
            };
            await store.SaveAsync(job);

            job.Status = AnalysisJobStatus.Cancelled;
            job.CompletedAtUtc = DateTime.UtcNow;
            Assert.True(await store.TryTransitionAsync(job, AnalysisJobStatus.Running));

            job.Status = AnalysisJobStatus.Succeeded;
            job.ResultJson = "{}";
            Assert.False(await store.TryTransitionAsync(job, AnalysisJobStatus.Running));

            var persisted = await store.GetAsync(job.Id);
            Assert.NotNull(persisted);
            Assert.Equal(AnalysisJobStatus.Cancelled, persisted!.Status);
            Assert.Null(persisted.ResultJson);
        }
        finally
        {
            TryDelete(databasePath);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }
}
