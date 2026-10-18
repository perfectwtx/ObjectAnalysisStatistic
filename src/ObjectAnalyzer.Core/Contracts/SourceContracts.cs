using System.Collections.Immutable;
using System.Runtime.CompilerServices;

namespace ObjectAnalyzer.Parsing;

public enum SourcePositionKind { None, Record, PhysicalLine, Row }
public enum MalformedRecordPolicy { Stop, Skip }

public sealed record SourceCapabilities(bool Streaming, bool Replayable, bool CanResume,
    bool NativeAsync, bool RootProjection, bool CanSkipMalformed, SourcePositionKind PositionKind, bool RequiresSeek = false);

public sealed record SourceDescriptor(string Name, string Format, SourceCapabilities Capabilities);
public sealed record SourceLocation(string Source, long? Record = null, long? Line = null,
    long? Row = null, string? Sheet = null, string? Column = null);
public sealed record SourceDiagnostic(string Code, string Message, SourceLocation Location, long Occurrences = 1);

public sealed class SourceReadException(string message, SourceLocation location, Exception? inner = null)
    : IOException(message, inner)
{
    public SourceLocation Location { get; } = location;
}

/// <summary>Detached typed record. Keys are structural paths, never unescaped display names.</summary>
public sealed class SourceRecord
{
    private static readonly Comparer<FieldPath> FieldOrder = Comparer<FieldPath>.Create((left, right) => StringComparer.Ordinal.Compare(left.ToString(), right.ToString()));
    public ImmutableSortedDictionary<FieldPath, RecordValue> Fields { get; }
    public SourceLocation Location { get; }

    public SourceRecord(IEnumerable<KeyValuePair<FieldPath, RecordValue>> fields, SourceLocation location)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(location);
        Fields = fields.ToImmutableSortedDictionary(FieldOrder);
        if (Fields.Values.Any(value => value is null)) throw new ArgumentException("Use RecordValue.Null for null field values.", nameof(fields));
        Location = location;
    }

    public static SourceRecord FromValue(RecordValue value, SourceLocation location, bool flatten = true)
    {
        try { return new(RecordNormalizer.Normalize(value, flatten), location); }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
        { throw new SourceReadException("Input record must be a valid object.", location, exception); }
    }

    internal static SourceRecord FromLegacy(Dictionary<string, object?> record, SourceLocation location)
    {
        try { return new(record.Select(pair => KeyValuePair.Create(FieldPath.Parse(pair.Key), RecordValue.FromObject(pair.Value))), location); }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or NotSupportedException or FormatException)
        { throw new SourceReadException("Input record normalization failed.", location, exception); }
    }

    // The existing processing bridge retains RecordValue instances; normalization in the engine is a no-op.
    internal Dictionary<string, object?> ToEngineRecord()
        => Fields.ToDictionary(pair => pair.Key.ToString(), pair => (object?)pair.Value, StringComparer.Ordinal);
    public Dictionary<string, object?> ToObjectDictionary()
        => Fields.ToDictionary(pair => pair.Key.ToString(), pair => pair.Value.ToObject(), StringComparer.Ordinal);
}

/// <summary>Sampling belongs to the execution pipeline, never a parser or source.</summary>
public sealed record SourceReadOptions
{
    public AnalysisResourceLimits ResourceLimits { get; init; } = new();
    public Action? CheckMemory { get; init; }
    public string? Format { get; internal init; }
    public bool Flatten { get; init; } = true;
    public bool CsvInferNumbers { get; init; } = true;
    public IReadOnlyList<ValueParserConfig> ValueParsers { get; init; } = Array.Empty<ValueParserConfig>();
    public long StartPosition { get; init; }
    public string? RootPath { get; init; }
    public MalformedRecordPolicy MalformedRecords { get; init; }
    public int DiagnosticLimit { get; init; } = 100;
    public Action<SourceDiagnostic>? DiagnosticSink { get; init; }
    public Action<long>? PositionProgress { get; init; }
    public Action<long>? SkippedRecordProgress { get; init; }

    internal void Validate(SourceDescriptor descriptor)
    {
        ResourceLimits.Validate();
        ArgumentOutOfRangeException.ThrowIfNegative(StartPosition);
        ArgumentOutOfRangeException.ThrowIfNegative(DiagnosticLimit);
        if (!Enum.IsDefined(MalformedRecords)) throw new ArgumentException("Unknown malformed-record policy.", nameof(descriptor));
        if (StartPosition > 0 && !descriptor.Capabilities.CanResume)
            throw new NotSupportedException($"Source '{descriptor.Name}' does not support position resume.");
        if (MalformedRecords == MalformedRecordPolicy.Skip && !descriptor.Capabilities.CanSkipMalformed)
            throw new NotSupportedException($"Format '{descriptor.Format}' cannot safely skip malformed records.");
        if (!string.IsNullOrWhiteSpace(RootPath) && !descriptor.Capabilities.RootProjection)
            throw new NotSupportedException($"Format '{descriptor.Format}' does not support root projection.");
    }
}

public interface IAnalysisDataSource
{
    SourceDescriptor Descriptor { get; }
    IEnumerable<SourceRecord> Read(SourceReadOptions options, CancellationToken cancellationToken = default);
    IAsyncEnumerable<SourceRecord> ReadAsync(SourceReadOptions options, CancellationToken cancellationToken = default)
        => SourceAsync.FromSync(Read(options, cancellationToken), cancellationToken);
}

public interface IRecordParser
{
    IReadOnlyList<string> Formats { get; }
    IReadOnlyList<string> Extensions { get; }
    SourceCapabilities Capabilities { get; }
    SourceCapabilities GetCapabilities(string format) => Capabilities;
    IEnumerable<SourceRecord> Parse(Stream stream, string source, SourceReadOptions options,
        CancellationToken cancellationToken = default);
    IAsyncEnumerable<SourceRecord> ParseAsync(Stream stream, string source, SourceReadOptions options,
        CancellationToken cancellationToken = default)
        => SourceAsync.FromSync(Parse(stream, source, options, cancellationToken), cancellationToken);
}

internal static class SourceAsync
{
    public static async IAsyncEnumerable<SourceRecord> FromSync(IEnumerable<SourceRecord> records,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        foreach (var record in records) { cancellationToken.ThrowIfCancellationRequested(); yield return record; }
    }
}

/// <summary>Parser lookup port; concrete registrations belong to input adapters.</summary>
public interface IRecordParserRegistry
{
    IRecordParser Resolve(string format);
    string DetectFormat(string path);
    string FormatFromExtension(string extension);
}
