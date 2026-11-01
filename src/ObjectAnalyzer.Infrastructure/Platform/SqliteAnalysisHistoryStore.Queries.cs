using Microsoft.Data.Sqlite;

namespace ObjectAnalyzer.Platform;

public sealed partial class SqliteAnalysisHistoryStore
{
    public async Task<AnalysisJob?> GetPreviousSucceededAsync(string sourceName, Guid excludedJobId,
        DateTime? startedBeforeUtc = null, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        connection.CreateCollation("OA_ORDINAL_IGNORE_CASE", StringComparer.OrdinalIgnoreCase.Compare);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, source_name, source_type, status, started_at_utc, completed_at_utc, duration_ms,
                total_objects, field_count, quality_score, issue_count, error_message, result_json,
                gate_results_json, quality_baseline_evaluation_json
            FROM analysis_jobs
            WHERE source_name = $source COLLATE OA_ORDINAL_IGNORE_CASE AND status = $succeeded AND id <> $excluded
                AND ($before IS NULL OR started_at_utc < $before)
            ORDER BY COALESCE(completed_at_utc, started_at_utc) DESC, started_at_utc DESC, id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$source", sourceName);
        command.Parameters.AddWithValue("$succeeded", (int)AnalysisJobStatus.Succeeded);
        command.Parameters.AddWithValue("$excluded", excludedJobId.ToString("D"));
        command.Parameters.AddWithValue("$before", (object?)startedBeforeUtc?.ToString("O") ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadJob(reader) : null;
    }

    public async Task<AnalysisJob?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, source_name, source_type, status, started_at_utc, completed_at_utc, duration_ms, total_objects, field_count, quality_score, issue_count, error_message, result_json, gate_results_json, quality_baseline_evaluation_json FROM analysis_jobs WHERE id = $id";
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
        command.CommandText = "SELECT id, source_name, source_type, status, started_at_utc, completed_at_utc, duration_ms, total_objects, field_count, quality_score, issue_count, error_message, result_json, gate_results_json, quality_baseline_evaluation_json FROM analysis_jobs ORDER BY started_at_utc DESC LIMIT $limit";
        command.Parameters.AddWithValue("$limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<AnalysisJob>();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(ReadJob(reader));
        return result;
    }

    public async Task<IReadOnlyList<AnalysisJob>> GetRecentSummariesAsync(int limit = 50, int offset = 0, AnalysisJobStatus? status = null, CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 500);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, source_name, source_type, status, started_at_utc, completed_at_utc, duration_ms, total_objects, field_count, quality_score, issue_count, error_message, NULL AS result_json, gate_results_json, quality_baseline_evaluation_json FROM analysis_jobs WHERE ($status IS NULL OR status = $status) ORDER BY started_at_utc DESC, id LIMIT $limit OFFSET $offset";
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", Math.Max(0, offset));
        command.Parameters.AddWithValue("$status", status.HasValue ? (object)(int)status.Value : DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<AnalysisJob>();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(ReadJob(reader));
        return result;
    }

    private static AnalysisJob ReadJob(SqliteDataReader reader)
    {
        return new AnalysisJob
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
            ResultJson = reader.IsDBNull(12) ? null : reader.GetString(12),
            GateResultsJson = reader.IsDBNull(13) ? null : reader.GetString(13),
            QualityBaselineEvaluationJson = reader.IsDBNull(14) ? null : reader.GetString(14)
        };
    }
}
