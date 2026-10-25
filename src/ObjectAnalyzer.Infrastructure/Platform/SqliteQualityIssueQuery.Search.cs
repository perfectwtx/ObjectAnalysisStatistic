using System.Globalization;
using Microsoft.Data.Sqlite;

namespace ObjectAnalyzer.Platform;

public sealed partial class SqliteQualityIssueQuery
{
    public async Task<QualityIssueSearchPage> SearchAsync(QualityIssueSearch search, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(search);
        await using var connection = await OpenSearchConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: true);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var cte = ConfigureSearch(command, search);
        command.CommandText = cte + "SELECT COUNT(*) FROM filtered;";
        var total = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        command.CommandText = cte + """
            SELECT job_id, field_name, check_type, issue_count, message, issue_status,
                owner, comment, updated_at_utc, started_at_utc
            FROM filtered
            ORDER BY issue_count DESC, field_name COLLATE OA_ORDINAL_IGNORE_CASE,
                check_type COLLATE OA_ORDINAL_IGNORE_CASE, started_at_utc DESC, job_id
            LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(search.Limit, 1, 500));
        command.Parameters.AddWithValue("$offset", Math.Max(0, search.Offset));
        var items = new List<QualityIssueSearchItem>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var jobId = Guid.Parse(reader.GetString(0));
                var field = reader.GetString(1);
                var check = reader.GetString(2);
                items.Add(new QualityIssueSearchItem(
                    new QualityIssue(jobId, field, check, reader.GetInt64(3), reader.GetString(4)),
                    new QualityIssueResponsibility(jobId, field, check,
                        reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetString(5),
                        reader.IsDBNull(7) ? null : reader.GetString(7),
                        reader.IsDBNull(8) ? null : ParseDate(reader.GetString(8))),
                    ParseDate(reader.GetString(9))));
            }
        }
        await transaction.CommitAsync(cancellationToken);
        return new QualityIssueSearchPage(items, total);
    }

    public async Task<QualityIssueSummary> SummarizeAsync(QualityIssueSearch search, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(search);
        await using var connection = await OpenSearchConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = ConfigureSearch(command, search) + """
            SELECT 'total' AS dimension, '' AS value, COUNT(*) AS n, COALESCE(SUM(issue_count),0) AS violations FROM filtered
            UNION ALL SELECT 'severity', severity, COUNT(*), 0 FROM filtered GROUP BY severity COLLATE OA_ORDINAL_IGNORE_CASE
            UNION ALL SELECT 'check', check_type, COUNT(*), 0 FROM filtered GROUP BY check_type COLLATE OA_ORDINAL_IGNORE_CASE
            UNION ALL SELECT 'field', field_name, COUNT(*), 0 FROM filtered GROUP BY field_name COLLATE OA_ORDINAL_IGNORE_CASE
            UNION ALL SELECT 'status', issue_status, COUNT(*), 0 FROM filtered GROUP BY issue_status COLLATE OA_ORDINAL_IGNORE_CASE
            ORDER BY dimension, n DESC, value COLLATE OA_ORDINAL_IGNORE_CASE;
            """;
        var groups = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal)
        {
            ["severity"] = new(StringComparer.OrdinalIgnoreCase), ["check"] = new(StringComparer.OrdinalIgnoreCase),
            ["field"] = new(StringComparer.OrdinalIgnoreCase), ["status"] = new(StringComparer.OrdinalIgnoreCase)
        };
        var total = 0;
        long violations = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var dimension = reader.GetString(0);
            if (dimension == "total") { total = reader.GetInt32(2); violations = reader.GetInt64(3); }
            else groups[dimension][reader.GetString(1)] = reader.GetInt32(2);
        }
        return new QualityIssueSummary(total, violations, groups["severity"], groups["check"], groups["field"], groups["status"]);
    }

    private async Task<SqliteConnection> OpenSearchConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            connection.CreateCollation("OA_ORDINAL_IGNORE_CASE", StringComparer.OrdinalIgnoreCase.Compare);
            return connection;
        }
        catch { await connection.DisposeAsync(); throw; }
    }

    private static string ConfigureSearch(SqliteCommand command, QualityIssueSearch search)
    {
        var cases = new List<string>();
        foreach (var (check, severity) in QualityIssueSeverity.ByCheck)
        {
            var index = cases.Count;
            cases.Add($"WHEN i.check_type = $check{index} THEN $severity{index}");
            command.Parameters.AddWithValue($"$check{index}", check);
            command.Parameters.AddWithValue($"$severity{index}", severity);
        }
        command.Parameters.AddWithValue("$succeeded", (int)AnalysisJobStatus.Succeeded);
        command.Parameters.AddWithValue("$jobId", (object?)search.JobId?.ToString("D") ?? DBNull.Value);
        command.Parameters.AddWithValue("$severity", Normalize(search.Severity));
        command.Parameters.AddWithValue("$check", Normalize(search.Check));
        command.Parameters.AddWithValue("$status", Normalize(search.Status));
        return $"""
            WITH issue_rows AS (
                SELECT i.*, j.started_at_utc, COALESCE(r.status, 'Open') AS issue_status,
                    r.owner, r.comment, r.updated_at_utc,
                    CASE {string.Join(" ", cases)} ELSE 'low' END AS severity
                FROM quality_issues i JOIN analysis_jobs j ON j.id = i.job_id
                LEFT JOIN quality_issue_responsibilities r ON r.rowid = (
                    SELECT rowid FROM quality_issue_responsibilities
                    WHERE job_id = i.job_id AND field_name = i.field_name COLLATE OA_ORDINAL_IGNORE_CASE
                        AND check_type = i.check_type COLLATE OA_ORDINAL_IGNORE_CASE
                    ORDER BY updated_at_utc DESC, rowid DESC LIMIT 1
                )
                WHERE j.status = $succeeded AND ($jobId IS NULL OR j.id = $jobId)
            ), filtered AS (
                SELECT * FROM issue_rows
                WHERE ($severity IS NULL OR severity = $severity COLLATE OA_ORDINAL_IGNORE_CASE)
                    AND ($check IS NULL OR check_type = $check COLLATE OA_ORDINAL_IGNORE_CASE)
                    AND ($status IS NULL OR issue_status = $status COLLATE OA_ORDINAL_IGNORE_CASE)
            )

            """;
    }

    private static object Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;
    private static DateTime ParseDate(string value) => DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
