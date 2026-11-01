using Microsoft.Data.Sqlite;

namespace ObjectAnalyzer.Platform;

public sealed partial class SqliteAnalysisHistoryStore
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS analysis_jobs (
                id TEXT PRIMARY KEY,
                source_name TEXT NOT NULL,
                source_type TEXT NOT NULL,
                status INTEGER NOT NULL,
                started_at_utc TEXT NOT NULL,
                completed_at_utc TEXT NULL,
                duration_ms INTEGER NOT NULL,
                total_objects INTEGER NOT NULL,
                field_count INTEGER NOT NULL,
                quality_score REAL NULL,
                issue_count INTEGER NOT NULL,
                error_message TEXT NULL,
                result_json TEXT NULL,
                gate_results_json TEXT NULL,
                quality_baseline_evaluation_json TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS remediation_verification_outcomes (
                job_id TEXT PRIMARY KEY, previous_job_id TEXT NULL,
                succeeded INTEGER NOT NULL, error TEXT NULL, attempted_at_utc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_analysis_jobs_started_at
                ON analysis_jobs(started_at_utc DESC);
            CREATE TABLE IF NOT EXISTS quality_snapshots (
                job_id TEXT PRIMARY KEY,
                source_name TEXT NOT NULL,
                occurred_at_utc TEXT NOT NULL,
                total_objects INTEGER NOT NULL,
                field_count INTEGER NOT NULL,
                issue_count INTEGER NOT NULL,
                completeness REAL NOT NULL,
                validity REAL NOT NULL,
                uniqueness REAL NOT NULL,
                consistency REAL NOT NULL,
                anomaly_control REAL NOT NULL,
                overall_score REAL NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_quality_snapshots_source_time
                ON quality_snapshots(source_name, occurred_at_utc DESC);
            CREATE TABLE IF NOT EXISTS field_quality_snapshots (
                job_id TEXT NOT NULL,
                field_name TEXT NOT NULL,
                coverage REAL NOT NULL,
                null_rate REAL NOT NULL,
                uniqueness REAL NULL,
                outlier_ratio REAL NULL,
                PRIMARY KEY (job_id, field_name)
            );
            CREATE INDEX IF NOT EXISTS ix_field_quality_snapshots_field_time
                ON field_quality_snapshots(field_name, job_id);
            CREATE TABLE IF NOT EXISTS quality_issues (
                job_id TEXT NOT NULL,
                field_name TEXT NOT NULL,
                check_type TEXT NOT NULL,
                issue_count INTEGER NOT NULL,
                message TEXT NOT NULL,
                PRIMARY KEY (job_id, field_name, check_type)
            );
            CREATE INDEX IF NOT EXISTS ix_quality_issues_job
                ON quality_issues(job_id);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);

        // 兼容已经存在的旧数据库：CREATE TABLE IF NOT EXISTS 不会给旧表补列。
        foreach (var column in new[] { "gate_results_json", "quality_baseline_evaluation_json" })
        {
            await using var migration = connection.CreateCommand();
            migration.CommandText = $"ALTER TABLE analysis_jobs ADD COLUMN {column} TEXT NULL";
            try
            {
                await migration.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 1 && ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
            {
                // 已升级，忽略。
            }
        }
    }
}
