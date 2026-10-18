using System.Runtime.CompilerServices;

namespace ObjectAnalyzer.Parsing;

/// <summary>Factories for equivalent file, byte-stream and typed-record input contracts.</summary>
public static class DataSources
{
    public static IAnalysisDataSource File(string path, string? format = null, RecordParserRegistry? registry = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        registry ??= RecordParserRegistry.Default;
        format = (format ?? registry.DetectFormat(path)).Trim().ToLowerInvariant();
        var parser = registry.Resolve(format);
        var capabilities = parser.GetCapabilities(format) with
        {
            CanResume = parser.GetCapabilities(format).CanResume && CompressionDetector.Detect(path) == CompressionType.None
        };
        if (CompressionDetector.Detect(path) != CompressionType.None && capabilities.RequiresSeek)
            capabilities = capabilities with { Streaming = false };
        return new ParsedSource(new SourceDescriptor(path, format, capabilities), parser,
            token => { var input = InputSource.Open(path, materializeExcel: false, cancellationToken: token); return new StreamLease(input.Content, input); });
    }

    /// <summary>The factory must return a fresh stream per enumeration when replayable is true.</summary>
    public static IAnalysisDataSource Stream(Func<Stream> open, string format, string name = "stream",
        RecordParserRegistry? registry = null, bool replayable = true, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(open);
        ArgumentException.ThrowIfNullOrWhiteSpace(format);
        format = format.Trim().ToLowerInvariant();
        var parser = (registry ?? RecordParserRegistry.Default).Resolve(format);
        var capability = parser.GetCapabilities(format);
        int used = 0;
        return new ParsedSource(new SourceDescriptor(name, format, capability with
        { Replayable = replayable, CanResume = replayable && capability.CanResume,
          RequiresSeek = capability.RequiresSeek,
          Streaming = capability.Streaming && !capability.RequiresSeek }), parser, _ =>
        {
            if (!replayable && Interlocked.Exchange(ref used, 1) != 0) throw new InvalidOperationException("The stream source is single-use.");
            var stream = open();
            return new StreamLease(stream, leaveOpen ? null : stream);
        });
    }

    public static IAnalysisDataSource Stream(Stream stream, string format, string name = "stream",
        bool leaveOpen = false, RecordParserRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return Stream(() => stream, format, name, registry, replayable: false, leaveOpen: leaveOpen);
    }

    public static IAnalysisDataSource Records(IEnumerable<RecordValue> records, string name = "records", bool replayable = false)
    { ArgumentNullException.ThrowIfNull(records); return new RecordSource(records, null, name, replayable); }
    public static IAnalysisDataSource AsyncRecords(IAsyncEnumerable<RecordValue> records, string name = "records", bool replayable = false)
    { ArgumentNullException.ThrowIfNull(records); return new RecordSource(null, records, name, replayable); }
    public static IAnalysisDataSource Database(string connectionString, DatabaseProvider provider, string query)
        => new DatabaseDataSource(connectionString, provider, query);

    private sealed class StreamLease(Stream content, IDisposable? owner) : IDisposable, IAsyncDisposable
    {
        public Stream Content { get; } = content;
        public void Dispose() => owner?.Dispose();
        public ValueTask DisposeAsync()
        {
            if (owner is IAsyncDisposable asynchronous) return asynchronous.DisposeAsync();
            owner?.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ParsedSource(SourceDescriptor descriptor, IRecordParser parser, Func<CancellationToken, StreamLease> open) : IAnalysisDataSource
    {
        public SourceDescriptor Descriptor { get; } = descriptor;
        public IEnumerable<SourceRecord> Read(SourceReadOptions options, CancellationToken cancellationToken = default)
        {
            options = options with { Format = Descriptor.Format };
            options.Validate(Descriptor);
            cancellationToken.ThrowIfCancellationRequested();
            using var lease = open(cancellationToken);
            using var stream = new SourceReadStream(lease.Content, cancellationToken);
            using var buffered = Descriptor.Capabilities.RequiresSeek && !stream.CanSeek ? new MemoryStream() : null;
            if (buffered is not null) { stream.CopyTo(buffered); buffered.Position = 0; }
            foreach (var record in parser.Parse(buffered ?? (Stream)stream, Descriptor.Name, options, cancellationToken))
            { cancellationToken.ThrowIfCancellationRequested(); yield return record; }
        }
        public async IAsyncEnumerable<SourceRecord> ReadAsync(SourceReadOptions options,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            options = options with { Format = Descriptor.Format };
            options.Validate(Descriptor);
            cancellationToken.ThrowIfCancellationRequested();
            await using var lease = open(cancellationToken);
            using var stream = new SourceReadStream(lease.Content, cancellationToken);
            using var buffered = Descriptor.Capabilities.RequiresSeek && !stream.CanSeek ? new MemoryStream() : null;
            if (buffered is not null)
            { await stream.CopyToAsync(buffered, cancellationToken).ConfigureAwait(false); buffered.Position = 0; }
            await foreach (var record in parser.ParseAsync(buffered ?? (Stream)stream, Descriptor.Name, options, cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
            { cancellationToken.ThrowIfCancellationRequested(); yield return record; }
        }
    }

    private sealed class RecordSource(IEnumerable<RecordValue>? records, IAsyncEnumerable<RecordValue>? asyncRecords,
        string name, bool replayable) : IAnalysisDataSource
    {
        private int _used;
        public SourceDescriptor Descriptor { get; } = new(name, "records",
            new SourceCapabilities(true, replayable, false, asyncRecords is not null, false, false, SourcePositionKind.Record));
        private void Begin(SourceReadOptions options)
        {
            options = options with { Format = Descriptor.Format };
            options.Validate(Descriptor);
            if (!replayable && Interlocked.Exchange(ref _used, 1) != 0) throw new InvalidOperationException("The record source is single-use.");
        }
        public IEnumerable<SourceRecord> Read(SourceReadOptions options, CancellationToken cancellationToken = default)
        {
            if (records is null) throw new NotSupportedException("This source requires asynchronous execution.");
            Begin(options);
            long index = 0;
            foreach (var record in records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                index++;
            options.PositionProgress?.Invoke(index);
                yield return SourceRecord.FromValue(record, new SourceLocation(name, index), options.Flatten);
            }
        }
        public async IAsyncEnumerable<SourceRecord> ReadAsync(SourceReadOptions options,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (asyncRecords is null)
            {
                foreach (var record in Read(options, cancellationToken)) yield return record;
                yield break;
            }
            Begin(options);
            long index = 0;
            await foreach (var record in asyncRecords.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                index++;
            options.PositionProgress?.Invoke(index);
                yield return SourceRecord.FromValue(record, new SourceLocation(name, index), options.Flatten);
            }
        }
    }
}
