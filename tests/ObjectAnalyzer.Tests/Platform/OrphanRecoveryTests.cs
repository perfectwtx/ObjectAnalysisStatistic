using Microsoft.Data.Sqlite;
using ObjectAnalyzer.Platform;

namespace ObjectAnalyzer.Tests.Platform;

public sealed class OrphanRecoveryTests
{
    [Theory]
    [InlineData(AnalysisJobStatus.Pending)]
    [InlineData(AnalysisJobStatus.Running)]
    public async Task Recover_ProcessesAllOldJobsBeyondHistoryLimit(AnalysisJobStatus status)
    {
        var path = Path.Combine(Path.GetTempPath(), $"oa-recovery-{Guid.NewGuid():N}.db");
        try
        {
            var store = new SqliteAnalysisHistoryStore(path);
            await store.InitializeAsync();
            await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i<1101)
                INSERT INTO analysis_jobs (id, source_name, source_type, status, started_at_utc,
                    duration_ms, total_objects, field_count, issue_count)
                SELECT printf('00000000-0000-0000-0000-%012d', i), 'test', 'json',
                    CASE WHEN i<=500 THEN $succeeded ELSE $unfinished END,
                    CASE WHEN i<=500 THEN '2026-10-25T00:00:00Z' ELSE '2026-10-20T00:00:00Z' END,
                    0,0,0,0 FROM n;
                """;
            command.Parameters.AddWithValue("$succeeded", (int)AnalysisJobStatus.Succeeded);
            command.Parameters.AddWithValue("$unfinished", (int)status);
            await command.ExecuteNonQueryAsync();
            Assert.All(await store.GetRecentAsync(500), x => Assert.Equal(AnalysisJobStatus.Succeeded, x.Status));

            var completedAt = new DateTime(2026, 10, 25, 1, 0, 0, DateTimeKind.Utc);
            Assert.Equal(601, await store.RecoverOrphanedJobsAsync(completedAt, "recovered"));
            Assert.Equal(0, await store.RecoverOrphanedJobsAsync(completedAt, "recovered"));
            var oldest = await store.GetAsync(Guid.Parse("00000000-0000-0000-0000-000000001101"));
            Assert.Equal(AnalysisJobStatus.Failed, oldest!.Status);
            Assert.Equal(completedAt, oldest.CompletedAtUtc);
            Assert.Equal("recovered", oldest.ErrorMessage);
            Assert.True(oldest.DurationMs > 0);
        }
        finally { SqliteTestDatabase.Delete(path); }
    }
}
