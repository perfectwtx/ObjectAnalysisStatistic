using Microsoft.Data.Sqlite;

namespace ObjectAnalyzer.Platform;

/// <summary>SQLite 质量问题责任与处理状态存储。</summary>
public sealed class SqliteQualityIssueResponsibilityStore : IQualityIssueResponsibilityStore
{
    private static readonly SemaphoreSlim s_writeLock = new(1, 1);
    private readonly string _connectionString;

    public SqliteQualityIssueResponsibilityStore(string databasePath)
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
            CREATE TABLE IF NOT EXISTS quality_issue_responsibilities (
                job_id TEXT NOT NULL,
                field_name TEXT NOT NULL,
                check_type TEXT NOT NULL,
                owner TEXT NULL,
                status TEXT NOT NULL,
                comment TEXT NULL,
                updated_at_utc TEXT NOT NULL,
                PRIMARY KEY (job_id, field_name, check_type)
            );
            CREATE INDEX IF NOT EXISTS ix_quality_issue_responsibilities_owner
                ON quality_issue_responsibilities(owner);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<QualityIssueResponsibility>> GetByJobAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT job_id, field_name, check_type, owner, status, comment, updated_at_utc
            FROM quality_issue_responsibilities
            WHERE job_id = $jobId
            ORDER BY field_name, check_type;
            """;
        command.Parameters.AddWithValue("$jobId", jobId.ToString("D"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<QualityIssueResponsibility>();
        while (await reader.ReadAsync(cancellationToken))
            items.Add(Read(reader));

        return items;
    }

    public async Task<QualityIssueResponsibility?> GetAsync(
        Guid jobId,
        string fieldName,
        string check,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT job_id, field_name, check_type, owner, status, comment, updated_at_utc
            FROM quality_issue_responsibilities
            WHERE job_id = $jobId AND field_name = $fieldName AND check_type = $check;
            """;
        command.Parameters.AddWithValue("$jobId", jobId.ToString("D"));
        command.Parameters.AddWithValue("$fieldName", fieldName);
        command.Parameters.AddWithValue("$check", check);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task SaveAsync(
        QualityIssueResponsibility responsibility,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(responsibility);
        responsibility.Validate();

        await s_writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO quality_issue_responsibilities
                    (job_id, field_name, check_type, owner, status, comment, updated_at_utc)
                VALUES
                    ($jobId, $fieldName, $check, $owner, $status, $comment, $updatedAtUtc)
                ON CONFLICT(job_id, field_name, check_type) DO UPDATE SET
                    owner = excluded.owner,
                    status = excluded.status,
                    comment = excluded.comment,
                    updated_at_utc = excluded.updated_at_utc;
                """;
            command.Parameters.AddWithValue("$jobId", responsibility.JobId.ToString("D"));
            command.Parameters.AddWithValue("$fieldName", responsibility.FieldName);
            command.Parameters.AddWithValue("$check", responsibility.Check);
            command.Parameters.AddWithValue("$owner", (object?)responsibility.Owner ?? DBNull.Value);
            command.Parameters.AddWithValue("$status", responsibility.Status);
            command.Parameters.AddWithValue("$comment", (object?)responsibility.Comment ?? DBNull.Value);
            command.Parameters.AddWithValue("$updatedAtUtc", (responsibility.UpdatedAtUtc ?? DateTime.UtcNow).ToUniversalTime().ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            s_writeLock.Release();
        }
    }

    private static QualityIssueResponsibility Read(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        reader.GetString(1),
        reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        DateTime.Parse(reader.GetString(6), null, System.Globalization.DateTimeStyles.RoundtripKind));
}
