using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ObjectAnalyzer.Parsing;

internal sealed class JsonLinesRecordParser : IRecordParser
{
    public IReadOnlyList<string> Formats { get; } = ["jsonl", "ndjson"];
    public IReadOnlyList<string> Extensions { get; } = [".jsonl", ".ndjson"];
    public SourceCapabilities Capabilities { get; } = new(true, true, true, true, false, true, SourcePositionKind.PhysicalLine);

    public IEnumerable<SourceRecord> Parse(Stream stream, string source, SourceReadOptions options, CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, leaveOpen: true);
        var state = new LineState(source, options);
        foreach (var line in BoundedLines.Read(reader, options.ResourceLimits.MaxRecordCharacters, cancellationToken))
            if (state.Parse(line) is { } record) yield return record;
        state.Complete();
    }

    public async IAsyncEnumerable<SourceRecord> ParseAsync(Stream stream, string source, SourceReadOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, leaveOpen: true);
        var state = new LineState(source, options);
        await foreach (var line in BoundedLines.ReadAsync(reader, options.ResourceLimits.MaxRecordCharacters, cancellationToken))
            if (state.Parse(line) is { } record) yield return record;
        state.Complete();
    }

    private sealed class LineState(string source, SourceReadOptions options)
    {
        private long _line;
        private long _record;
        private long _skipped;

        public SourceRecord? Parse(string text)
        {
            options.CheckMemory?.Invoke();
            _line++;
            options.PositionProgress?.Invoke(_line);
            if (_line <= options.StartPosition || string.IsNullOrWhiteSpace(text)) return null;
            var location = new SourceLocation(source, ++_record, _line);
            try
            {
                using var document = JsonDocument.Parse(text);
                return SourceRecord.FromValue(RecordValue.FromJsonElement(document.RootElement), location, options.Flatten);
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidDataException or SourceReadException)
            {
                if (options.MalformedRecords == MalformedRecordPolicy.Stop)
                    throw new SourceReadException($"第 {_line} 行的 JSON 记录无效。", location, exception);
                _skipped++;
                options.SkippedRecordProgress?.Invoke(_skipped);
                if (_skipped <= options.DiagnosticLimit)
                    options.DiagnosticSink?.Invoke(new SourceDiagnostic("input.invalid-record", "Malformed record skipped.", location));
                return null;
            }
        }

        public void Complete()
        {
            if (_skipped > 0) options.DiagnosticSink?.Invoke(new SourceDiagnostic("input.skipped-summary",
                $"Skipped {_skipped} malformed records.", new SourceLocation(source, Line: _line), _skipped));
        }
    }
}

internal sealed class JsonRecordParser : IRecordParser
{
    public IReadOnlyList<string> Formats { get; } = ["json"];
    public IReadOnlyList<string> Extensions { get; } = [".json"];
    public SourceCapabilities Capabilities { get; } = new(false, true, false, true, true, false, SourcePositionKind.Record);

    public IEnumerable<SourceRecord> Parse(Stream stream, string source, SourceReadOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var document = Load(stream, source);
        foreach (var record in Project(document, source, options, cancellationToken)) yield return record;
    }

    public async IAsyncEnumerable<SourceRecord> ParseAsync(Stream stream, string source, SourceReadOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // JsonDocument buffers the document; the capability explicitly reports non-streaming.
        using var document = await LoadAsync(stream, source, cancellationToken).ConfigureAwait(false);
        foreach (var record in Project(document, source, options, cancellationToken)) yield return record;
    }

    private static async Task<JsonDocument> LoadAsync(Stream stream, string source, CancellationToken cancellationToken)
    {
        try
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, leaveOpen: true);
            return JsonDocument.Parse(await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (JsonException exception)
        { throw new SourceReadException("Invalid JSON document.", new SourceLocation(source, Line: exception.LineNumber + 1), exception); }
    }

    private static JsonDocument Load(Stream stream, string source)
    {
        try
        {
            // StreamReader handles UTF-8 BOM, preserving the existing file input contract.
            using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, leaveOpen: true);
            return JsonDocument.Parse(reader.ReadToEnd());
        }
        catch (JsonException exception) { throw new SourceReadException("Invalid JSON document.", new SourceLocation(source, Line: exception.LineNumber + 1), exception); }
    }

    private static IEnumerable<SourceRecord> Project(JsonDocument document, string source, SourceReadOptions options, CancellationToken cancellationToken)
    {
        long index = 0;
        if (!string.IsNullOrWhiteSpace(options.RootPath))
        {
            var node = JsonNode.Parse(document.RootElement.GetRawText());
            foreach (var record in JsonPathProjector.ApplyRootPath(node, options.RootPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                index++;
            options.PositionProgress?.Invoke(index);
                var typed = SourceRecord.FromLegacy(record, new SourceLocation(source, index));
                yield return options.Flatten
                    ? new SourceRecord(typed.Fields.SelectMany(pair => RecordNormalizer.Flatten(pair.Value, pair.Key)), typed.Location)
                    : typed;
            }
            yield break;
        }
        if (document.RootElement.ValueKind is not (JsonValueKind.Array or JsonValueKind.Object))
            throw new SourceReadException("JSON root must be an object or an array of objects.", new SourceLocation(source));
        var elements = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray().ToArray() : [document.RootElement];
        foreach (var element in elements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            index++;
            options.PositionProgress?.Invoke(index);
            var location = new SourceLocation(source, index);
            if (element.ValueKind != JsonValueKind.Object)
                throw new SourceReadException("Each JSON record must be an object.", location);
            yield return SourceRecord.FromValue(RecordValue.FromJsonElement(element), location, options.Flatten);
        }
    }
}
