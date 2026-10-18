using Microsoft.Data.Sqlite;

namespace ObjectAnalyzer.Platform;

/// <summary>基于 SQLite 标准化质量问题表的查询实现。</summary>
public sealed class SqliteQualityIssueQuery : IQualityIssueQuery
{
    private readonly string _connectionString;

    public SqliteQualityIssueQuery(string databasePath)
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

    public async Task<IReadOnlyList<QualityIssue>> GetByJobAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT job_id, field_name, check_type, issue_count, message
            FROM quality_issues
            WHERE job_id = $jobId
            ORDER BY issue_count DESC, field_name, check_type;
            """;
        command.Parameters.AddWithValue("$jobId", jobId.ToString("D"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var issues = new List<QualityIssue>();
        while (await reader.ReadAsync(cancellationToken))
        {
            issues.Add(new QualityIssue(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.GetString(4)));
        }

        return issues;
    }
}
