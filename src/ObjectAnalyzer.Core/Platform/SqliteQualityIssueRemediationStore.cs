using Microsoft.Data.Sqlite;

namespace ObjectAnalyzer.Platform;

/// <summary>SQLite 质量问题整改记录存储。</summary>
public sealed class SqliteQualityIssueRemediationStore : IQualityIssueRemediationStore
{
    private static readonly SemaphoreSlim s_writeLock = new(1, 1);
    private readonly string _connectionString;

    public SqliteQualityIssueRemediationStore(string databasePath)
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
            CREATE TABLE IF NOT EXISTS quality_issue_remediations (
                id TEXT PRIMARY KEY,
                job_id TEXT NOT NULL,
                field_name TEXT NOT NULL,
                check_type TEXT NOT NULL,
                action TEXT NOT NULL,
                status TEXT NOT NULL,
                due_at_utc TEXT NULL,
                completed_at_utc TEXT NULL,
                comment TEXT NULL,
                created_at_utc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_quality_issue_remediations_issue
                ON quality_issue_remediations(job_id, field_name, check_type);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<QualityIssueRemediation>> GetByIssueAsync(
        Guid jobId,
        string fieldName,
        string check,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, job_id, field_name, check_type, action, status,
                   due_at_utc, completed_at_utc, comment, created_at_utc
            FROM quality_issue_remediations
            WHERE job_id = $jobId AND field_name = $fieldName AND check_type = $check
            ORDER BY created_at_utc DESC;
            """;
        command.Parameters.AddWithValue("$jobId", jobId.ToString("D"));
        command.Parameters.AddWithValue("$fieldName", fieldName);
        command.Parameters.AddWithValue("$check", check);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<QualityIssueRemediation>();
        while (await reader.ReadAsync(cancellationToken))
            items.Add(Read(reader));
        return items;
    }

    public async Task SaveAsync(QualityIssueRemediation remediation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remediation);
        remediation.Validate();
        var createdAt = remediation.CreatedAtUtc == default ? DateTime.UtcNow : remediation.CreatedAtUtc.ToUniversalTime();

        await s_writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO quality_issue_remediations
                    (id, job_id, field_name, check_type, action, status,
                     due_at_utc, completed_at_utc, comment, created_at_utc)
                VALUES
                    ($id, $jobId, $fieldName, $check, $action, $status,
                     $dueAtUtc, $completedAtUtc, $comment, $createdAtUtc)
                ON CONFLICT(id) DO UPDATE SET
                    action = excluded.action,
                    status = excluded.status,
                    due_at_utc = excluded.due_at_utc,
                    completed_at_utc = excluded.completed_at_utc,
                    comment = excluded.comment;
                """;
            command.Parameters.AddWithValue("$id", remediation.Id.ToString("D"));
            command.Parameters.AddWithValue("$jobId", remediation.JobId.ToString("D"));
            command.Parameters.AddWithValue("$fieldName", remediation.FieldName);
            command.Parameters.AddWithValue("$check", remediation.Check);
            command.Parameters.AddWithValue("$action", remediation.Action);
            command.Parameters.AddWithValue("$status", remediation.Status);
            command.Parameters.AddWithValue("$dueAtUtc", remediation.DueAtUtc?.ToUniversalTime().ToString("O") ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$completedAtUtc", remediation.CompletedAtUtc?.ToUniversalTime().ToString("O") ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$comment", (object?)remediation.Comment ?? DBNull.Value);
            command.Parameters.AddWithValue("$createdAtUtc", createdAt.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            s_writeLock.Release();
        }
    }

    private static QualityIssueRemediation Read(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        Guid.Parse(reader.GetString(1)),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5),
        reader.IsDBNull(6) ? null : ParseDate(reader.GetString(6)),
        reader.IsDBNull(7) ? null : ParseDate(reader.GetString(7)),
        reader.IsDBNull(8) ? null : reader.GetString(8),
        ParseDate(reader.GetString(9)));

    private static DateTime ParseDate(string value) =>
        DateTime.Parse(value, null, System.Globalization.DateTimeStyles.RoundtripKind);
}
