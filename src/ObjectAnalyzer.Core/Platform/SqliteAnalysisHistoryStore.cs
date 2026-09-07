using Microsoft.Data.Sqlite;

namespace ObjectAnalyzer.Platform;

/// <summary>默认的本地作业历史存储。数据库文件默认位于应用数据目录。</summary>
public sealed class SqliteAnalysisHistoryStore : IAnalysisHistoryStore
{
    private readonly string _connectionString;

    public SqliteAnalysisHistoryStore(string databasePath)
    {
        var fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS analysis_jobs (
                id TEXT PRIMARY KEY,
                source_name TEXT NOT NULL,
                source_type TEXT NOT NULL,
                status INTEGER NOT NULL,
                started_at_utc TEXT NOT NULL,
                completed_at_utc TEXT NULL,
                duration_ms INTEGER NOT NULL,
                total_objects INTEGER NOT NULL,
                field_count INTEGER NOT NULL,
                quality_score REAL NULL,
                issue_count INTEGER NOT NULL,
                error_message TEXT NULL,
                result_json TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_analysis_jobs_started_at
                ON analysis_jobs(started_at_utc DESC);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveAsync(AnalysisJob job, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO analysis_jobs
            (id, source_name, source_type, status, started_at_utc, completed_at_utc,
             duration_ms, total_objects, field_count, quality_score, issue_count,
             error_message, result_json)
            VALUES
            ($id, $sourceName, $sourceType, $status, $startedAt, $completedAt,
             $duration, $totalObjects, $fieldCount, $qualityScore, $issueCount,
             $errorMessage, $resultJson)
            ON CONFLICT(id) DO UPDATE SET
                source_name = excluded.source_name,
                source_type = excluded.source_type,
                status = excluded.status,
                started_at_utc = excluded.started_at_utc,
                completed_at_utc = excluded.completed_at_utc,
                duration_ms = excluded.duration_ms,
                total_objects = excluded.total_objects,
                field_count = excluded.field_count,
                quality_score = excluded.quality_score,
                issue_count = excluded.issue_count,
                error_message = excluded.error_message,
                result_json = excluded.result_json;
            """;
        AddParameters(command, job);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<AnalysisJob?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM analysis_jobs WHERE id = $id";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadJob(reader) : null;
    }

    public async Task<IReadOnlyList<AnalysisJob>> GetRecentAsync(int limit = 50, CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 500);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM analysis_jobs ORDER BY started_at_utc DESC LIMIT $limit";
        command.Parameters.AddWithValue("$limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<AnalysisJob>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadJob(reader));
        return result;
    }

    private static void AddParameters(SqliteCommand command, AnalysisJob job)
    {
        command.Parameters.AddWithValue("$id", job.Id.ToString("D"));
        command.Parameters.AddWithValue("$sourceName", job.SourceName);
        command.Parameters.AddWithValue("$sourceType", job.SourceType);
        command.Parameters.AddWithValue("$status", (int)job.Status);
        command.Parameters.AddWithValue("$startedAt", job.StartedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$completedAt", (object?)job.CompletedAtUtc?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$duration", job.DurationMs);
        command.Parameters.AddWithValue("$totalObjects", job.TotalObjects);
        command.Parameters.AddWithValue("$fieldCount", job.FieldCount);
        command.Parameters.AddWithValue("$qualityScore", (object?)job.QualityScore ?? DBNull.Value);
        command.Parameters.AddWithValue("$issueCount", job.IssueCount);
        command.Parameters.AddWithValue("$errorMessage", (object?)job.ErrorMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("$resultJson", (object?)job.ResultJson ?? DBNull.Value);
    }

    private static AnalysisJob ReadJob(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        SourceName = reader.GetString(1),
        SourceType = reader.GetString(2),
        Status = (AnalysisJobStatus)reader.GetInt32(3),
        StartedAtUtc = DateTime.Parse(reader.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind),
        CompletedAtUtc = reader.IsDBNull(5) ? null : DateTime.Parse(reader.GetString(5), null, System.Globalization.DateTimeStyles.RoundtripKind),
        DurationMs = reader.GetInt64(6),
        TotalObjects = reader.GetInt64(7),
        FieldCount = reader.GetInt32(8),
        QualityScore = reader.IsDBNull(9) ? null : reader.GetDouble(9),
        IssueCount = reader.GetInt32(10),
        ErrorMessage = reader.IsDBNull(11) ? null : reader.GetString(11),
        ResultJson = reader.IsDBNull(12) ? null : reader.GetString(12)
    };
}
