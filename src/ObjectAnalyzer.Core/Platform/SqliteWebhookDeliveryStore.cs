using Microsoft.Data.Sqlite;

namespace ObjectAnalyzer.Platform;

/// <summary>SQLite Webhook Outbox 存储。</summary>
public sealed class SqliteWebhookDeliveryStore : IWebhookDeliveryStore
{
    private readonly string _connectionString;

    public SqliteWebhookDeliveryStore(string databasePath)
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
            CREATE TABLE IF NOT EXISTS webhook_deliveries (
                id TEXT PRIMARY KEY,
                rule_id TEXT NOT NULL,
                job_id TEXT NOT NULL,
                event_type TEXT NOT NULL,
                webhook_url TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                status INTEGER NOT NULL,
                attempt_count INTEGER NOT NULL,
                next_attempt_at_utc TEXT NOT NULL,
                created_at_utc TEXT NOT NULL,
                sent_at_utc TEXT NULL,
                last_error TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_webhook_deliveries_pending
                ON webhook_deliveries(status, next_attempt_at_utc);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task EnqueueAsync(WebhookDelivery delivery, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO webhook_deliveries
            (id, rule_id, job_id, event_type, webhook_url, payload_json, status,
             attempt_count, next_attempt_at_utc, created_at_utc, sent_at_utc, last_error)
            VALUES ($id, $rule_id, $job_id, $event_type, $webhook_url, $payload_json, $status,
                    $attempt_count, $next_attempt_at_utc, $created_at_utc, $sent_at_utc, $last_error);
            """;
        AddParameters(command, delivery);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<WebhookDelivery?> GetNextPendingAsync(DateTime utcNow, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, rule_id, job_id, event_type, webhook_url, payload_json, status,
                   attempt_count, next_attempt_at_utc, created_at_utc, sent_at_utc, last_error
            FROM webhook_deliveries
            WHERE (status = $pending OR status = $processing)
              AND next_attempt_at_utc <= $now
            ORDER BY next_attempt_at_utc, created_at_utc
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$pending", (int)WebhookDeliveryStatus.Pending);
        command.Parameters.AddWithValue("$processing", (int)WebhookDeliveryStatus.Processing);
        command.Parameters.AddWithValue("$now", utcNow.ToString("O"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return Read(reader);
    }

    public async Task MarkProcessingAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await UpdateStatusAsync(id, WebhookDeliveryStatus.Processing, cancellationToken);
    }

    public async Task MarkSucceededAsync(Guid id, DateTime sentAtUtc, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE webhook_deliveries
            SET status = $status, sent_at_utc = $sent_at_utc, last_error = NULL
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$status", (int)WebhookDeliveryStatus.Succeeded);
        command.Parameters.AddWithValue("$sent_at_utc", sentAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$id", id.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkFailedAsync(Guid id, string error, DateTime nextAttemptAtUtc, bool permanent, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE webhook_deliveries
            SET status = $status,
                attempt_count = attempt_count + 1,
                next_attempt_at_utc = $next_attempt_at_utc,
                last_error = $last_error
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$status", (int)(permanent ? WebhookDeliveryStatus.Failed : WebhookDeliveryStatus.Pending));
        command.Parameters.AddWithValue("$next_attempt_at_utc", nextAttemptAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$last_error", error.Length > 2000 ? error[..2000] : error);
        command.Parameters.AddWithValue("$id", id.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task UpdateStatusAsync(Guid id, WebhookDeliveryStatus status, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE webhook_deliveries SET status = $status WHERE id = $id;";
        command.Parameters.AddWithValue("$status", (int)status);
        command.Parameters.AddWithValue("$id", id.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddParameters(SqliteCommand command, WebhookDelivery d)
    {
        command.Parameters.AddWithValue("$id", d.Id.ToString());
        command.Parameters.AddWithValue("$rule_id", d.RuleId.ToString());
        command.Parameters.AddWithValue("$job_id", d.JobId.ToString());
        command.Parameters.AddWithValue("$event_type", d.EventType);
        command.Parameters.AddWithValue("$webhook_url", d.WebhookUrl);
        command.Parameters.AddWithValue("$payload_json", d.PayloadJson);
        command.Parameters.AddWithValue("$status", (int)d.Status);
        command.Parameters.AddWithValue("$attempt_count", d.AttemptCount);
        command.Parameters.AddWithValue("$next_attempt_at_utc", d.NextAttemptAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$created_at_utc", d.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$sent_at_utc", (object?)d.SentAtUtc?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$last_error", (object?)d.LastError ?? DBNull.Value);
    }

    private static WebhookDelivery Read(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        RuleId = Guid.Parse(reader.GetString(1)),
        JobId = Guid.Parse(reader.GetString(2)),
        EventType = reader.GetString(3),
        WebhookUrl = reader.GetString(4),
        PayloadJson = reader.GetString(5),
        Status = (WebhookDeliveryStatus)reader.GetInt32(6),
        AttemptCount = reader.GetInt32(7),
        NextAttemptAtUtc = DateTime.Parse(reader.GetString(8), null, System.Globalization.DateTimeStyles.RoundtripKind),
        CreatedAtUtc = DateTime.Parse(reader.GetString(9), null, System.Globalization.DateTimeStyles.RoundtripKind),
        SentAtUtc = reader.IsDBNull(10) ? null : DateTime.Parse(reader.GetString(10), null, System.Globalization.DateTimeStyles.RoundtripKind),
        LastError = reader.IsDBNull(11) ? null : reader.GetString(11)
    };
}
