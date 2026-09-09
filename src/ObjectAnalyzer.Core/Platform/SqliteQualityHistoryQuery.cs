using Microsoft.Data.Sqlite;

namespace ObjectAnalyzer.Platform;

/// <summary>基于 SQLite 标准化质量快照表的查询实现。</summary>
public sealed class SqliteQualityHistoryQuery : IQualityHistoryQuery
{
    private readonly string _connectionString;

    public SqliteQualityHistoryQuery(string databasePath)
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

    public async Task<IReadOnlyList<QualitySnapshot>> GetSnapshotsAsync(
        string? sourceName = null,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 1000);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT job_id, source_name, occurred_at_utc, total_objects, field_count, issue_count,
                   completeness, validity, uniqueness, consistency, anomaly_control, overall_score
            FROM quality_snapshots
            WHERE ($sourceName IS NULL OR source_name = $sourceName)
            ORDER BY occurred_at_utc DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$sourceName", (object?)sourceName ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var snapshots = new List<QualitySnapshot>();
        while (await reader.ReadAsync(cancellationToken))
        {
            snapshots.Add(new QualitySnapshot(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                DateTime.Parse(reader.GetString(2), null, System.Globalization.DateTimeStyles.RoundtripKind),
                reader.GetInt64(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                new QualityMetrics(
                    reader.GetDouble(6),
                    reader.GetDouble(7),
                    reader.GetDouble(8),
                    reader.GetDouble(9),
                    reader.GetDouble(10))));
        }

        return snapshots;
    }

    public async Task<IReadOnlyList<FieldQualitySnapshot>> GetFieldsByJobAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT job_id, field_name, coverage, null_rate, uniqueness, outlier_ratio
            FROM field_quality_snapshots
            WHERE job_id = $jobId
            ORDER BY field_name;
            """;
        command.Parameters.AddWithValue("$jobId", jobId.ToString("D"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var fields = new List<FieldQualitySnapshot>();
        while (await reader.ReadAsync(cancellationToken))
        {
            fields.Add(new FieldQualitySnapshot(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetDouble(2),
                reader.GetDouble(3),
                reader.IsDBNull(4) ? null : reader.GetDouble(4),
                reader.IsDBNull(5) ? null : reader.GetDouble(5)));
        }

        return fields;
    }
}
