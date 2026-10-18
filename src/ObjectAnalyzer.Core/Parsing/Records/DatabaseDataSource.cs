using System.Data.Common;
using System.Runtime.CompilerServices;

namespace ObjectAnalyzer.Parsing;

internal sealed class DatabaseDataSource(string connectionString, DatabaseProvider provider, string query) : IAnalysisDataSource
{
    public SourceDescriptor Descriptor { get; } = new("database:" + provider, "db",
        new SourceCapabilities(true, true, false, true, false, false, SourcePositionKind.Row));

    public IEnumerable<SourceRecord> Read(SourceReadOptions options, CancellationToken cancellationToken = default)
    {
        options.Validate(Descriptor);
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = DatabaseReader.CreateConnection(connectionString, provider);
        connection.Open();
        using var command = connection.CreateCommand();
        Configure(command);
        using var registration = cancellationToken.Register(() => Cancel(command));
        using var reader = command.ExecuteReader();
        long index = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!reader.Read()) break;
            index++;
            options.PositionProgress?.Invoke(index);
            yield return ReadRecord(reader, index, options.Flatten);
        }
    }

    public async IAsyncEnumerable<SourceRecord> ReadAsync(SourceReadOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options.Validate(Descriptor);
        cancellationToken.ThrowIfCancellationRequested();
        await using var connection = DatabaseReader.CreateConnection(connectionString, provider);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        Configure(command);
        using var registration = cancellationToken.Register(() => Cancel(command));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        long index = 0;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            index++;
            options.PositionProgress?.Invoke(index);
            yield return ReadRecord(reader, index, options.Flatten);
        }
    }

    private void Configure(DbCommand command) { command.CommandText = query; command.CommandTimeout = 0; }
    private static void Cancel(DbCommand command)
    {
        try { command.Cancel(); }
        catch (Exception) { /* Some providers do not support Cancel; token checks remain active. */ }
    }
    private SourceRecord ReadRecord(DbDataReader reader, long index, bool flatten)
    {
        var fields = new Dictionary<string, RecordValue>(StringComparer.Ordinal);
        for (int i = 0; i < reader.FieldCount; i++)
        {
            string name = reader.GetName(i);
            if (!fields.TryAdd(name, RecordValue.FromObject(reader.GetValue(i))))
                throw new SourceReadException("Duplicate database column name.", new SourceLocation(Descriptor.Name, index, Row: index, Column: name));
        }
        return SourceRecord.FromValue(RecordValue.FromObject(fields), new SourceLocation(Descriptor.Name, index, Row: index), flatten);
    }
}
