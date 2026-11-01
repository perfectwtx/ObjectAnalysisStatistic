using Microsoft.Data.Sqlite;

namespace ObjectAnalyzer.Platform;

public sealed partial class SqliteAnalysisHistoryStore
{
    public async Task<int> RecoverOrphanedJobsAsync(DateTime completedAtUtc, string errorMessage, CancellationToken cancellationToken = default)
    {
        await s_writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE analysis_jobs
                SET status = $failed, completed_at_utc = $completedAt,
                    duration_ms = MAX(0, CAST((julianday($completedAt) - julianday(started_at_utc)) * 86400000 AS INTEGER)),
                    error_message = $error
                WHERE status IN ($pending, $running);
                """;
            command.Parameters.AddWithValue("$failed", (int)AnalysisJobStatus.Failed);
            command.Parameters.AddWithValue("$pending", (int)AnalysisJobStatus.Pending);
            command.Parameters.AddWithValue("$running", (int)AnalysisJobStatus.Running);
            command.Parameters.AddWithValue("$completedAt", completedAtUtc.ToString("O"));
            command.Parameters.AddWithValue("$error", errorMessage);
            return await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally { s_writeLock.Release(); }
    }

    public async Task RecordRemediationVerificationAsync(RemediationVerificationOutcome outcome, CancellationToken cancellationToken = default)
    {
        await s_writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO remediation_verification_outcomes(job_id, previous_job_id, succeeded, error, attempted_at_utc)
                SELECT $id, $previous, $succeeded, $error, $at
                WHERE EXISTS (SELECT 1 FROM analysis_jobs WHERE id = $id AND status = $status)
                ON CONFLICT(job_id) DO UPDATE SET previous_job_id = excluded.previous_job_id,
                    succeeded = excluded.succeeded, error = excluded.error, attempted_at_utc = excluded.attempted_at_utc;
                """;
            command.Parameters.AddWithValue("$id", outcome.JobId.ToString("D"));
            command.Parameters.AddWithValue("$previous", (object?)outcome.PreviousJobId?.ToString("D") ?? DBNull.Value);
            command.Parameters.AddWithValue("$succeeded", outcome.Succeeded ? 1 : 0);
            command.Parameters.AddWithValue("$error", (object?)outcome.Error ?? DBNull.Value);
            command.Parameters.AddWithValue("$at", outcome.AttemptedAtUtc.ToString("O"));
            command.Parameters.AddWithValue("$status", (int)AnalysisJobStatus.Succeeded);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally { s_writeLock.Release(); }
    }

    public async Task<RemediationVerificationOutcome?> GetRemediationVerificationAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT previous_job_id, succeeded, error, attempted_at_utc FROM remediation_verification_outcomes WHERE job_id = $id";
        command.Parameters.AddWithValue("$id", jobId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new RemediationVerificationOutcome(jobId, reader.IsDBNull(0) ? null : Guid.Parse(reader.GetString(0)),
                reader.GetBoolean(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                DateTime.Parse(reader.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind)) : null;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var idText = id.ToString("D");
        await s_writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = connection.BeginTransaction();
            try
            {
                // 先删关联表（表可能尚未创建时忽略）
                foreach (var sql in new[]
                {
                    "DELETE FROM quality_issue_responsibilities WHERE job_id = $id",
                    "DELETE FROM QualityIssueRemediations WHERE JobId = $id",
                    "DELETE FROM quality_issues WHERE job_id = $id",
                    "DELETE FROM field_quality_snapshots WHERE job_id = $id",
                    "DELETE FROM quality_snapshots WHERE job_id = $id",
                    "DELETE FROM remediation_verification_outcomes WHERE job_id = $id",
                    "DELETE FROM webhook_deliveries WHERE job_id = $id",
                })
                {
                    try
                    {
                        await using var cmd = connection.CreateCommand();
                        cmd.Transaction = transaction;
                        cmd.CommandText = sql;
                        cmd.Parameters.AddWithValue("$id", idText);
                        await cmd.ExecuteNonQueryAsync(cancellationToken);
                    }
                    catch (SqliteException ex) when (ex.SqliteErrorCode == 1 &&
                        ex.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase))
                    {
                        // 可选表尚未初始化，跳过
                    }
                }

                await using var delJob = connection.CreateCommand();
                delJob.Transaction = transaction;
                delJob.CommandText = "DELETE FROM analysis_jobs WHERE id = $id";
                delJob.Parameters.AddWithValue("$id", idText);
                var removed = await delJob.ExecuteNonQueryAsync(cancellationToken);

                await transaction.CommitAsync(cancellationToken);
                return removed > 0;
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
        }
        finally
        {
            s_writeLock.Release();
        }
    }
}
