using Microsoft.Data.Sqlite;

namespace ObjectAnalyzer.Platform;

/// <summary>SQLite 告警规则存储。</summary>
public sealed class SqliteAlertRuleStore : IAlertRuleStore
{
    private readonly string _connectionString;

    public SqliteAlertRuleStore(string databasePath)
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
            CREATE TABLE IF NOT EXISTS alert_rules (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                enabled INTEGER NOT NULL,
                quality_score_below REAL NULL,
                completeness_below REAL NULL,
                critical_issue INTEGER NOT NULL,
                breaking_schema_change INTEGER NOT NULL,
                distribution_drift_above REAL NULL,
                webhook_url TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_alert_rules_enabled
                ON alert_rules(enabled);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AlertRule>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM alert_rules ORDER BY name";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<AlertRule>();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(ReadRule(reader));
        return result;
    }

    public async Task<AlertRule?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM alert_rules WHERE id = $id";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadRule(reader) : null;
    }

    public async Task SaveAsync(AlertRule rule, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO alert_rules
            (id, name, enabled, quality_score_below, completeness_below,
             critical_issue, breaking_schema_change, distribution_drift_above, webhook_url)
            VALUES
            ($id, $name, $enabled, $qualityScoreBelow, $completenessBelow,
             $criticalIssue, $breakingSchemaChange, $distributionDriftAbove, $webhookUrl)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name,
                enabled = excluded.enabled,
                quality_score_below = excluded.quality_score_below,
                completeness_below = excluded.completeness_below,
                critical_issue = excluded.critical_issue,
                breaking_schema_change = excluded.breaking_schema_change,
                distribution_drift_above = excluded.distribution_drift_above,
                webhook_url = excluded.webhook_url;
            """;
        command.Parameters.AddWithValue("$id", rule.Id.ToString("D"));
        command.Parameters.AddWithValue("$name", rule.Name);
        command.Parameters.AddWithValue("$enabled", rule.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$qualityScoreBelow", (object?)rule.QualityScoreBelow ?? DBNull.Value);
        command.Parameters.AddWithValue("$completenessBelow", (object?)rule.CompletenessBelow ?? DBNull.Value);
        command.Parameters.AddWithValue("$criticalIssue", rule.CriticalIssue ? 1 : 0);
        command.Parameters.AddWithValue("$breakingSchemaChange", rule.BreakingSchemaChange ? 1 : 0);
        command.Parameters.AddWithValue("$distributionDriftAbove", (object?)rule.DistributionDriftAbove ?? DBNull.Value);
        command.Parameters.AddWithValue("$webhookUrl", (object?)rule.WebhookUrl ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM alert_rules WHERE id = $id";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static AlertRule ReadRule(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        Name = reader.GetString(1),
        Enabled = reader.GetInt32(2) != 0,
        QualityScoreBelow = reader.IsDBNull(3) ? null : reader.GetDouble(3),
        CompletenessBelow = reader.IsDBNull(4) ? null : reader.GetDouble(4),
        CriticalIssue = reader.GetInt32(5) != 0,
        BreakingSchemaChange = reader.GetInt32(6) != 0,
        DistributionDriftAbove = reader.IsDBNull(7) ? null : reader.GetDouble(7),
        WebhookUrl = reader.IsDBNull(8) ? null : reader.GetString(8)
    };
}
