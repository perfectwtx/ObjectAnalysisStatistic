using Microsoft.Data.Sqlite;

namespace ObjectAnalyzer.Platform;

/// <summary>默认的本地作业历史存储。数据库文件默认位于应用数据目录。</summary>
public sealed partial class SqliteAnalysisHistoryStore : IAnalysisHistoryStore
{
    private static readonly SemaphoreSlim s_writeLock = new(1, 1);
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

    public async Task SaveAsync(AnalysisJob job, CancellationToken cancellationToken = default)
    {
        await s_writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO analysis_jobs
                (id, source_name, source_type, status, started_at_utc, completed_at_utc,
                 duration_ms, total_objects, field_count, quality_score, issue_count,
                 error_message, result_json, gate_results_json, quality_baseline_evaluation_json)
                VALUES
                ($id, $sourceName, $sourceType, $status, $startedAt, $completedAt,
                 $duration, $totalObjects, $fieldCount, $qualityScore, $issueCount,
                 $errorMessage, $resultJson, $gateResultsJson, $qualityBaselineEvaluationJson)
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
                    result_json = excluded.result_json,
                    gate_results_json = excluded.gate_results_json,
                    quality_baseline_evaluation_json = excluded.quality_baseline_evaluation_json;
                """;
            AddParameters(command, job);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            s_writeLock.Release();
        }
    }

    public async Task<bool> TryTransitionAsync(AnalysisJob job, AnalysisJobStatus expectedStatus, CancellationToken cancellationToken = default)
    {
        await s_writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            return await TryTransitionCoreAsync(connection, null, job, expectedStatus, cancellationToken);
        }
        finally
        {
            s_writeLock.Release();
        }
    }

    public async Task<bool> TryCompleteAsync(AnalysisCompletion completion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(completion);
        if (completion.Job.Status != AnalysisJobStatus.Succeeded || completion.Snapshot.JobId != completion.Job.Id ||
            completion.Fields.Any(x => x.JobId != completion.Job.Id) ||
            completion.Issues.Any(x => x.JobId != completion.Job.Id) ||
            completion.Deliveries.Any(x => x.JobId != completion.Job.Id))
            throw new ArgumentException("Completion records must belong to the succeeded job.", nameof(completion));

        await s_writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = connection.BeginTransaction();
            if (!await TryTransitionCoreAsync(connection, transaction, completion.Job, AnalysisJobStatus.Running, cancellationToken))
                return false;

            await SaveQualitySnapshotCoreAsync(connection, transaction, completion.Snapshot,
                completion.Fields, completion.Issues, cancellationToken);
            foreach (var delivery in completion.Deliveries)
                await SqliteWebhookDeliveryStore.EnqueueCoreAsync(connection, transaction, delivery, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        finally
        {
            s_writeLock.Release();
        }
    }

    private static async Task<bool> TryTransitionCoreAsync(SqliteConnection connection, SqliteTransaction? transaction,
        AnalysisJob job, AnalysisJobStatus expectedStatus, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE analysis_jobs SET
                source_name = $sourceName,
                source_type = $sourceType,
                status = $status,
                started_at_utc = $startedAt,
                completed_at_utc = $completedAt,
                duration_ms = $duration,
                total_objects = $totalObjects,
                field_count = $fieldCount,
                quality_score = $qualityScore,
                issue_count = $issueCount,
                error_message = $errorMessage,
                result_json = $resultJson,
                gate_results_json = $gateResultsJson,
                quality_baseline_evaluation_json = $qualityBaselineEvaluationJson
            WHERE id = $id AND status = $expectedStatus;
            """;
        AddParameters(command, job);
        command.Parameters.AddWithValue("$expectedStatus", (int)expectedStatus);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
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
        command.Parameters.AddWithValue("$gateResultsJson", (object?)job.GateResultsJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$qualityBaselineEvaluationJson", (object?)job.QualityBaselineEvaluationJson ?? DBNull.Value);
    }
}
