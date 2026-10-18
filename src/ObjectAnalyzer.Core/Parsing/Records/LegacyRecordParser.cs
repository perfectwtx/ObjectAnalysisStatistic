namespace ObjectAnalyzer.Parsing;

/// <summary>Boundary adapter for third-party XML/YAML/spreadsheet readers.</summary>
internal sealed class LegacyRecordParser(IFileParser parser, bool streaming, SourcePositionKind positionKind) : IRecordParser
{
    internal IFileParser LegacyParser => parser;
    public IReadOnlyList<string> Formats => parser.SupportedFormats;
    public IReadOnlyList<string> Extensions => parser.SupportedExtensions;
    public SourceCapabilities Capabilities { get; } = new(streaming, true, false, false, false, false, positionKind, parser.SupportedFormats.Any(format => format is "excel" or "excel-json"));

    public SourceCapabilities GetCapabilities(string format) => Capabilities with
    { RequiresSeek = format != "csv" && Capabilities.RequiresSeek };

    private IEnumerator<Dictionary<string, object?>> Open(Stream stream, string source, ParseOptions options)
    {
        try { return parser.Parse(stream, options).GetEnumerator(); }
        catch (Exception exception) when (exception is not (OperationCanceledException or SourceReadException))
        { throw new SourceReadException("Input parser initialization failed.", new SourceLocation(source), exception); }
    }

    public IEnumerable<SourceRecord> Parse(Stream stream, string source, SourceReadOptions options, CancellationToken cancellationToken = default)
    {
        long position = 0;
        long index = 0;
        var settings = new ParseOptions
        {
            Flatten = options.Flatten, CsvInferNumbers = options.CsvInferNumbers, ValueParsers = options.ValueParsers,
            SourceProgress = value => { cancellationToken.ThrowIfCancellationRequested(); position = value; options.PositionProgress?.Invoke(value); }
        };
        using var iterator = Open(stream, source, settings);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool next;
            try { next = iterator.MoveNext(); }
            catch (Exception exception) when (exception is not (OperationCanceledException or SourceReadException))
            {
                throw new SourceReadException("Input parsing failed.", new SourceLocation(source, index + 1,
                    Row: positionKind == SourcePositionKind.Row && position > 0 ? position : null), exception);
            }
            if (!next) break;
            index++;
            options.PositionProgress?.Invoke(position > 0 ? position : index);
            // XML historically ignored Flatten. Apply it at this boundary like the other formats.
            var record = SourceRecord.FromLegacy(iterator.Current, new SourceLocation(source, index,
                Row: positionKind == SourcePositionKind.Row ? position : null));
            yield return options.Flatten
                ? new SourceRecord(record.Fields.SelectMany(pair => RecordNormalizer.Flatten(pair.Value, pair.Key)), record.Location)
                : record;
        }
    }
}
