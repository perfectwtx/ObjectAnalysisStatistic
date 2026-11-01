using Microsoft.Data.Sqlite;

namespace ObjectAnalyzer.Platform;

public sealed partial class SqliteAnalysisHistoryStore
{
    public async Task SaveQualitySnapshotAsync(QualitySnapshot snapshot, IReadOnlyList<FieldQualitySnapshot> fields, IReadOnlyList<QualityIssue> issues, CancellationToken cancellationToken = default)
    {
        await s_writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = connection.BeginTransaction();
            try
            {
                await SaveQualitySnapshotCoreAsync(connection, transaction, snapshot, fields, issues, cancellationToken);

                await transaction.CommitAsync(cancellationToken);
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

    private static async Task SaveQualitySnapshotCoreAsync(SqliteConnection connection, SqliteTransaction transaction,
        QualitySnapshot snapshot, IReadOnlyList<FieldQualitySnapshot> fields, IReadOnlyList<QualityIssue> issues,
        CancellationToken cancellationToken)
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
}
