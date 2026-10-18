using Microsoft.Data.Sqlite;

namespace ObjectAnalyzer.Platform;

/// <summary>SQLite 数据质量基线存储。</summary>
public sealed class SqliteQualityBaselineStore : IQualityBaselineStore
{
    private static readonly SemaphoreSlim s_writeLock = new(1, 1);
    private readonly string _connectionString;

    public SqliteQualityBaselineStore(string databasePath)
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
            CREATE TABLE IF NOT EXISTS quality_baselines (
                source_name TEXT PRIMARY KEY,
                minimum_overall_score REAL NULL,
                minimum_completeness REAL NULL,
                minimum_validity REAL NULL,
                minimum_uniqueness REAL NULL,
                minimum_consistency REAL NULL,
                minimum_anomaly_control REAL NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<QualityBaseline?> GetAsync(string sourceName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT source_name, minimum_overall_score, minimum_completeness,
                   minimum_validity, minimum_uniqueness, minimum_consistency,
                   minimum_anomaly_control
            FROM quality_baselines
            WHERE source_name = $sourceName;
            """;
        command.Parameters.AddWithValue("$sourceName", sourceName);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<IReadOnlyList<QualityBaseline>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT source_name, minimum_overall_score, minimum_completeness,
                   minimum_validity, minimum_uniqueness, minimum_consistency,
                   minimum_anomaly_control
            FROM quality_baselines
            ORDER BY source_name;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<QualityBaseline>();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(Read(reader));
        return result;
    }

    public async Task SaveAsync(QualityBaseline baseline, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        baseline.Validate();

        await s_writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO quality_baselines
                (source_name, minimum_overall_score, minimum_completeness,
                 minimum_validity, minimum_uniqueness, minimum_consistency,
                 minimum_anomaly_control)
                VALUES
                ($sourceName, $overall, $completeness, $validity, $uniqueness,
                 $consistency, $anomalyControl)
                ON CONFLICT(source_name) DO UPDATE SET
                    minimum_overall_score = excluded.minimum_overall_score,
                    minimum_completeness = excluded.minimum_completeness,
                    minimum_validity = excluded.minimum_validity,
                    minimum_uniqueness = excluded.minimum_uniqueness,
                    minimum_consistency = excluded.minimum_consistency,
                    minimum_anomaly_control = excluded.minimum_anomaly_control;
                """;
            AddParameters(command, baseline);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            s_writeLock.Release();
        }
    }

    public async Task DeleteAsync(string sourceName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);

        await s_writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM quality_baselines WHERE source_name = $sourceName";
            command.Parameters.AddWithValue("$sourceName", sourceName);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            s_writeLock.Release();
        }
    }

    private static QualityBaseline Read(SqliteDataReader reader)
        => new(
            reader.GetString(0),
            GetNullableDouble(reader, 1),
            GetNullableDouble(reader, 2),
            GetNullableDouble(reader, 3),
            GetNullableDouble(reader, 4),
            GetNullableDouble(reader, 5),
            GetNullableDouble(reader, 6));

    private static double? GetNullableDouble(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);

    private static void AddParameters(SqliteCommand command, QualityBaseline baseline)
    {
        command.Parameters.AddWithValue("$sourceName", baseline.SourceName);
        command.Parameters.AddWithValue("$overall", (object?)baseline.MinimumOverallScore ?? DBNull.Value);
        command.Parameters.AddWithValue("$completeness", (object?)baseline.MinimumCompleteness ?? DBNull.Value);
        command.Parameters.AddWithValue("$validity", (object?)baseline.MinimumValidity ?? DBNull.Value);
        command.Parameters.AddWithValue("$uniqueness", (object?)baseline.MinimumUniqueness ?? DBNull.Value);
        command.Parameters.AddWithValue("$consistency", (object?)baseline.MinimumConsistency ?? DBNull.Value);
        command.Parameters.AddWithValue("$anomalyControl", (object?)baseline.MinimumAnomalyControl ?? DBNull.Value);
    }
}
