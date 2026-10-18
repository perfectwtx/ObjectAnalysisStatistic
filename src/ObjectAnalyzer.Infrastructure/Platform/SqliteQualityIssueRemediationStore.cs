using Microsoft.Data.Sqlite;

namespace ObjectAnalyzer.Platform;

/// <summary>基于 SQLite 的质量问题整改记录存储。</summary>
public sealed class SqliteQualityIssueRemediationStore : IQualityIssueRemediationStore
{
    private static readonly SemaphoreSlim s_writeLock = new(1, 1);
    private readonly string _connectionString;

    public SqliteQualityIssueRemediationStore(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS QualityIssueRemediations (
                Id TEXT PRIMARY KEY,
                JobId TEXT NOT NULL,
                FieldName TEXT NOT NULL,
                CheckName TEXT NOT NULL,
                Action TEXT NOT NULL,
                Status TEXT NOT NULL,
                DueAtUtc TEXT NULL,
                CompletedAtUtc TEXT NULL,
                Comment TEXT NULL,
                CreatedAtUtc TEXT NOT NULL
            );
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
            SELECT Id, JobId, FieldName, CheckName, Action, Status,
                   DueAtUtc, CompletedAtUtc, Comment, CreatedAtUtc
            FROM QualityIssueRemediations
            WHERE JobId = $jobId AND FieldName = $fieldName AND CheckName = $check
            ORDER BY CreatedAtUtc DESC;
            """;
        command.Parameters.AddWithValue("$jobId", jobId.ToString());
        command.Parameters.AddWithValue("$fieldName", fieldName);
        command.Parameters.AddWithValue("$check", check);

        var result = new List<QualityIssueRemediation>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(Read(reader));
        return result;
    }

    public async Task SaveAsync(QualityIssueRemediation remediation, CancellationToken cancellationToken = default)
    {
        remediation.Validate();
        var createdAt = remediation.CreatedAtUtc == default ? DateTime.UtcNow : remediation.CreatedAtUtc;

        await s_writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO QualityIssueRemediations
                    (Id, JobId, FieldName, CheckName, Action, Status, DueAtUtc, CompletedAtUtc, Comment, CreatedAtUtc)
                VALUES
                    ($id, $jobId, $fieldName, $check, $action, $status, $dueAtUtc, $completedAtUtc, $comment, $createdAtUtc)
                ON CONFLICT(Id) DO UPDATE SET
                    JobId = excluded.JobId,
                    FieldName = excluded.FieldName,
                    CheckName = excluded.CheckName,
                    Action = excluded.Action,
                    Status = excluded.Status,
                    DueAtUtc = excluded.DueAtUtc,
                    CompletedAtUtc = excluded.CompletedAtUtc,
                    Comment = excluded.Comment,
                    CreatedAtUtc = excluded.CreatedAtUtc;
                """;
            command.Parameters.AddWithValue("$id", remediation.Id.ToString());
            command.Parameters.AddWithValue("$jobId", remediation.JobId.ToString());
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

    private static QualityIssueRemediation Read(SqliteDataReader reader)
    {
        var remediation = new QualityIssueRemediation(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.IsDBNull(6) ? null : ParseDate(reader.GetString(6)),
            reader.IsDBNull(7) ? null : ParseDate(reader.GetString(7)),
            reader.IsDBNull(8) ? null : reader.GetString(8))
        {
            CreatedAtUtc = ParseDate(reader.GetString(9))
        };

        return remediation;
    }

    private static DateTime ParseDate(string value) =>
        DateTime.Parse(value, null, System.Globalization.DateTimeStyles.RoundtripKind);
}
