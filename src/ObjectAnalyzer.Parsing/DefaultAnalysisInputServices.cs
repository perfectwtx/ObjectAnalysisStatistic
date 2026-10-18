using ObjectAnalyzer.Analysis.Pipeline;

namespace ObjectAnalyzer.Parsing;

/// <summary>Built-in input adapters, explicitly installed by the hosting composition.</summary>
public sealed class DefaultAnalysisInputServices : IAnalysisInputServices
{
    public IAnalysisDataSource Open(AnalysisOptions options, AnalysisRules? rules, IRecordParserRegistry? parsers)
    {
        var registry = parsers ?? RecordParserRegistry.Default;
        if (!string.IsNullOrWhiteSpace(options.Input.ConnectionString))
            return DataSources.Database(options.Input.ConnectionString,
                DatabaseReader.ResolveProvider(options.Input.DbProvider, options.Input.ConnectionString), options.Input.Query!);
        if (string.IsNullOrWhiteSpace(options.Input.InputPath)) throw new ArgumentException("An input source is required.");
        string format = !string.IsNullOrWhiteSpace(options.Input.Format)
            ? options.Input.Format.Trim().ToLowerInvariant() : registry.DetectFormat(options.Input.InputPath);
        if (rules?.ValueParsers is { Count: > 0 } && format is "excel" or "excel-json" or "csv") format = "excel-json";
        return DataSources.File(options.Input.InputPath, format, registry);
    }
    public static string ResolveEffectiveFormat(string inputPath, AnalysisRules? rules, string? formatOverride)
    {
        string format = !string.IsNullOrWhiteSpace(formatOverride) ? formatOverride.Trim().ToLowerInvariant() : FileParsers.DetectFormat(inputPath);
        return rules?.ValueParsers is { Count: > 0 } && format is "excel" or "excel-json" or "csv" ? "excel-json" : format;
    }
    public void ValidateProjection(string expression) => JsonPathProjector.TryValidate(expression);
    public IReadOnlyList<Dictionary<string, object?>> ProjectRecord(Dictionary<string, object?> record, string expression)
        => JsonPathProjector.ApplyRecordPath(record, expression);
    public IReadOnlyDictionary<string, object?>? DescribeCompression(string path)
    {
        var compression = CompressionDetector.Detect(path);
        if (compression == CompressionType.None) return null;
        var data = new Dictionary<string, object?> { ["compression"] = compression.ToString() };
        if (compression == CompressionType.Zip && InputSource.PreviewZipEntries(path) is { } preview)
        { data["entry"] = preview.Chosen; data["ignoredEntries"] = preview.Others.Count; }
        return data;
    }
    public IRecordTransformer CreateTransformer(AnalysisRules? rules, IEnumerable<string>? fields, string? filter)
        => new FieldTransformProcessor(rules, fields, filter);
}
