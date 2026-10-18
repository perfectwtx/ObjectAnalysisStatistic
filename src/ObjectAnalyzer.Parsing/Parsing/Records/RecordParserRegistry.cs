using System.Collections.Immutable;
using ObjectAnalyzer.Parsing.Parsers;

namespace ObjectAnalyzer.Parsing;

/// <summary>Immutable parser and detector registrations, shared only by explicit selection.</summary>
public sealed class RecordParserRegistry : IRecordParserRegistry
{
    private readonly ImmutableArray<IRecordParser> _parsers;
    private readonly ImmutableArray<Func<string, string?>> _detectors;
    public static RecordParserRegistry Default { get; } = new([
        new JsonRecordParser(), new JsonLinesRecordParser(),
        new LegacyRecordParser(new ExcelCsvFileParser(), true, SourcePositionKind.Row),
        new LegacyRecordParser(new XmlFileParser(), false, SourcePositionKind.Record),
        new LegacyRecordParser(new YamlFileParser(), false, SourcePositionKind.Record),
        new LegacyRecordParser(new ExcelJsonFileParser(), true, SourcePositionKind.Row)
    ], []);

    public RecordParserRegistry(IEnumerable<IRecordParser> parsers)
        : this(parsers.ToImmutableArray(), []) { }

    private RecordParserRegistry(ImmutableArray<IRecordParser> parsers, ImmutableArray<Func<string, string?>> detectors)
    {
        _parsers = parsers.Select(parser => parser is RegisteredParser ? parser : new RegisteredParser(parser)).ToImmutableArray();
        _detectors = detectors;
        var formats = _parsers.SelectMany(parser => parser.Formats).ToArray();
        if (_parsers.Any(parser => parser.Formats.Count == 0) || formats.Any(string.IsNullOrWhiteSpace) || formats.Distinct(StringComparer.OrdinalIgnoreCase).Count() != formats.Length)
            throw new ArgumentException("Parser formats must be nonempty and unique.");
    }

    public RecordParserRegistry With(IRecordParser parser)
    { ArgumentNullException.ThrowIfNull(parser); return new(_parsers.Add(parser), _detectors); }
    public RecordParserRegistry WithDetector(Func<string, string?> detector)
    { ArgumentNullException.ThrowIfNull(detector); return new(_parsers, _detectors.Insert(0, detector)); }
    public IRecordParser Resolve(string format) => _parsers.FirstOrDefault(parser =>
        parser.Formats.Contains(format.Trim(), StringComparer.OrdinalIgnoreCase))
        ?? throw new NotSupportedException($"Unsupported record format: {format}");

    public string FormatFromExtension(string extension)
    {
        if (extension.Equals(".csv", StringComparison.OrdinalIgnoreCase)) return "csv";
        if (extension.Equals(".txt", StringComparison.OrdinalIgnoreCase)) return "jsonl";
        return _parsers.FirstOrDefault(parser => parser.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))?.Formats[0] ?? "json";
    }

    public string DetectFormat(string path)
    {
        foreach (var detector in _detectors)
            if (detector(path) is { } format) { Resolve(format); return format; }
        var compression = CompressionDetector.Detect(path);
        if (compression == CompressionType.Zip && InputSource.PreviewZipEntries(path) is { } entries)
            return FormatFromExtension(Path.GetExtension(CompressionDetector.StripGzipBzip2Chain(entries.Chosen)));
        if (compression == CompressionType.None && !CompressionDetector.HasKnownDataExtension(path)
            && CompressionDetector.LooksLikeOfficeOpenXmlPackage(path)) return "excel";
        return FormatFromExtension(Path.GetExtension(CompressionDetector.StripCompression(path)));
    }
}
