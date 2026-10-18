namespace ObjectAnalyzer.Analysis.Pipeline;

/// <summary>Input, projection and transformation ports. The execution engine never selects concrete adapters.</summary>
public interface IAnalysisInputServices
{
    IAnalysisDataSource Open(AnalysisOptions options, AnalysisRules? rules, IRecordParserRegistry? parsers);
    void ValidateProjection(string expression);
    IReadOnlyList<Dictionary<string, object?>> ProjectRecord(Dictionary<string, object?> record, string expression);
    IReadOnlyDictionary<string, object?>? DescribeCompression(string path);
    IRecordTransformer CreateTransformer(AnalysisRules? rules, IEnumerable<string>? fields, string? filter);
}

public interface IRecordTransformer
{
    bool HasSelectionOrTransforms { get; }
    IEnumerable<Dictionary<string, object?>> Wrap(IEnumerable<Dictionary<string, object?>> objects);
    void ApplyFailures(AnalysisResult result);
}
