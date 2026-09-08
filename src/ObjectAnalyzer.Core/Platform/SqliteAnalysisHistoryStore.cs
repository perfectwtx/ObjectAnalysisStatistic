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
            CREATE TABLE IF NOT EXISTS quality_snapshots (
                job_id TEXT PRIMARY KEY,
                source_name TEXT NOT NULL,
                occurred_at_utc TEXT NOT NULL,
                total_objects INTEGER NOT NULL,
                field_count INTEGER NOT NULL,
                issue_count INTEGER NOT NULL,
                completeness REAL NOT NULL,
                validity REAL NOT NULL,
                uniqueness REAL NOT NULL,
                consistency REAL NOT NULL,
                anomaly_control REAL NOT NULL,
                overall_score REAL NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_quality_snapshots_source_time
                ON quality_snapshots(source_name, occurred_at_utc DESC);
            CREATE TABLE IF NOT EXISTS field_quality_snapshots (
                job_id TEXT NOT NULL,
                field_name TEXT NOT NULL,
                coverage REAL NOT NULL,
                null_rate REAL NOT NULL,
                uniqueness REAL NULL,
                outlier_ratio REAL NULL,
                PRIMARY KEY (job_id, field_name)
            );
            CREATE INDEX IF NOT EXISTS ix_field_quality_snapshots_field_time
                ON field_quality_snapshots(field_name, job_id);
            CREATE TABLE IF NOT EXISTS quality_issues (
                job_id TEXT NOT NULL,
                field_name TEXT NOT NULL,
                check_type TEXT NOT NULL,
                issue_count INTEGER NOT NULL,
                message TEXT NOT NULL,
                PRIMARY KEY (job_id, field_name, check_type)
            );
            CREATE INDEX IF NOT EXISTS ix_quality_issues_job
                ON quality_issues(job_id);
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

    public async Task SaveQualitySnapshotAsync(QualitySnapshot snapshot, IReadOnlyList<FieldQualitySnapshot> fields, IReadOnlyList<QualityIssue> issues, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await ExecuteAsync(connection, transaction, """
                INSERT INTO quality_snapshots
                (job_id, source_name, occurred_at_utc, total_objects, field_count, issue_count,
                 completeness, validity, uniqueness, consistency, anomaly_control, overall_score)
                VALUES
                ($jobId, $sourceName, $occurredAt, $totalObjects, $fieldCount, $issueCount,
                 $completeness, $validity, $uniqueness, $consistency, $anomalyControl, $overallScore)
                ON CONFLICT(job_id) DO UPDATE SET
                    source_name = excluded.source_name,
                    occurred_at_utc = excluded.occurred_at_utc,
                    total_objects = excluded.total_objects,
                    field_count = excluded.field_count,
                    issue_count = excluded.issue_count,
                    completeness = excluded.completeness,
                    validity = excluded.validity,
                    uniqueness = excluded.uniqueness,
                    consistency = excluded.consistency,
                    anomaly_control = excluded.anomaly_control,
                    overall_score = excluded.overall_score;
                """,
                new Dictionary<string, object?>
                {
                    ["$jobId"] = snapshot.JobId.ToString("D"), ["$sourceName"] = snapshot.SourceName,
                    ["$occurredAt"] = snapshot.OccurredAtUtc.ToString("O"), ["$totalObjects"] = snapshot.TotalObjects,
                    ["$fieldCount"] = snapshot.FieldCount, ["$issueCount"] = snapshot.IssueCount,
                    ["$completeness"] = snapshot.Metrics.Completeness, ["$validity"] = snapshot.Metrics.Validity,
                    ["$uniqueness"] = snapshot.Metrics.Uniqueness, ["$consistency"] = snapshot.Metrics.Consistency,
                    ["$anomalyControl"] = snapshot.Metrics.AnomalyControl, ["$overallScore"] = snapshot.Metrics.OverallScore
                }, cancellationToken);

            foreach (var field in fields)
            {
                await ExecuteAsync(connection, transaction, """
                    INSERT INTO field_quality_snapshots
                    (job_id, field_name, coverage, null_rate, uniqueness, outlier_ratio)
                    VALUES ($jobId, $fieldName, $coverage, $nullRate, $uniqueness, $outlierRatio)
                    ON CONFLICT(job_id, field_name) DO UPDATE SET
                        coverage = excluded.coverage, null_rate = excluded.null_rate,
                        uniqueness = excluded.uniqueness, outlier_ratio = excluded.outlier_ratio;
                    """,
                    new Dictionary<string, object?>
                    {
                        ["$jobId"] = field.JobId.ToString("D"), ["$fieldName"] = field.FieldName,
                        ["$coverage"] = field.Coverage, ["$nullRate"] = field.NullRate,
                        ["$uniqueness"] = (object?)field.Uniqueness ?? DBNull.Value,
                        ["$outlierRatio"] = (object?)field.OutlierRatio ?? DBNull.Value
                    }, cancellationToken);
            }

            foreach (var issue in issues)
            {
                await ExecuteAsync(connection, transaction, """
                    INSERT INTO quality_issues
                    (job_id, field_name, check_type, issue_count, message)
                    VALUES ($jobId, $fieldName, $checkType, $issueCount, $message)
                    ON CONFLICT(job_id, field_name, check_type) DO UPDATE SET
                        issue_count = excluded.issue_count, message = excluded.message;
                    """,
                    new Dictionary<string, object?>
                    {
                        ["$jobId"] = issue.JobId.ToString("D"), ["$fieldName"] = issue.FieldName,
                        ["$checkType"] = issue.Check, ["$issueCount"] = issue.Count, ["$message"] = issue.Message
                    }, cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Key, parameter.Value ?? DBNull.Value);
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
        Id = Guid.Parse(reader.GetString(0)), SourceName = reader.GetString(1), SourceType = reader.GetString(2),
        Status = (AnalysisJobStatus)reader.GetInt32(3),
        StartedAtUtc = DateTime.Parse(reader.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind),
        CompletedAtUtc = reader.IsDBNull(5) ? null : DateTime.Parse(reader.GetString(5), null, System.Globalization.DateTimeStyles.RoundtripKind),
        DurationMs = reader.GetInt64(6), TotalObjects = reader.GetInt64(7), FieldCount = reader.GetInt32(8),
        QualityScore = reader.IsDBNull(9) ? null : reader.GetDouble(9), IssueCount = reader.GetInt32(10),
        ErrorMessage = reader.IsDBNull(11) ? null : reader.GetString(11), ResultJson = reader.IsDBNull(12) ? null : reader.GetString(12)
    };
}
