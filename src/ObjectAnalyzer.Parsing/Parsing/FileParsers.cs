using ObjectAnalyzer.Parsing.Parsers;

namespace ObjectAnalyzer.Parsing;

/// <summary>Compatibility adapter. New code uses IAnalysisDataSource and RecordParserRegistry.</summary>
public static class FileParsers
{
    public static string DetectFormat(string path)
    { var format = RecordParserRegistry.Default.DetectFormat(path); return format == "csv" ? "excel" : format; }
    public static string FormatFromExtension(string extension)
    { var format = RecordParserRegistry.Default.FormatFromExtension(extension); return format == "csv" ? "excel" : format; }

    public static IEnumerable<Dictionary<string, object?>> ParseByFormat(string path, string format, ParseOptions options)
    {
        IRecordParser parser;
        try { parser = RecordParserRegistry.Default.Resolve(format); }
        catch (NotSupportedException exception) { throw new InvalidOperationException($"不支持的文件格式: {format}", exception); }
        // Retain third-party parser-specific legacy errors, sampling and header behavior for existing callers.
        if (parser is RegisteredParser { Inner: LegacyRecordParser legacy }) return new DecompressingEnumerable(path, legacy.LegacyParser, options);
        var records = ReadTyped(path, format, options);
        return Sampling.IsEnabled(options.SampleReservoir, options.SampleStep)
            ? Sampling.Apply(records, options.SampleReservoir, options.SampleStep) : records;
    }

    private static IEnumerable<Dictionary<string, object?>> ReadTyped(string path, string format, ParseOptions options)
    {
        var settings = new SourceReadOptions
        {
            Flatten = options.Flatten, StartPosition = options.StartFromLine, PositionProgress = options.SourceProgress,
            CsvInferNumbers = options.CsvInferNumbers, ValueParsers = options.ValueParsers
        };
        using var iterator = DataSources.File(path, format).Read(settings).GetEnumerator();
        while (true)
        {
            bool next;
            try { next = iterator.MoveNext(); }
            catch (SourceReadException exception) { throw new InvalidDataException(exception.Message, exception); }
            if (!next) break;
            yield return iterator.Current.ToObjectDictionary();
        }
    }
}
