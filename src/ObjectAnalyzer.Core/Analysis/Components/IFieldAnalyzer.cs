using System.Text.Json;

namespace ObjectAnalyzer.Analysis;

/// <summary>Immutable, thread-safe factory. Each worker/field receives its own mutable state.</summary>
public interface IFieldAnalyzer
{
    string Id { get; }
    int Version { get; }
    string ConfigurationKey => "";
    IReadOnlyCollection<RecordValueKind> InputKinds { get; }
    bool IsEnabled(FieldAnalyzerContext context) => true;
    IFieldAnalyzerState CreateState(FieldAnalyzerContext context);
    IFieldAnalyzerState RestoreState(FieldAnalyzerContext context, JsonElement data);
}

public interface IFieldAnalyzerState
{
    void Observe(RecordValue value);
    /// <summary>Merge without modifying or retaining mutable references to the source state.</summary>
    void Merge(IFieldAnalyzerState other);
    /// <summary>Project detached results. Custom outputs belong in FieldStatistic.AnalyzerResults.</summary>
    void Complete(FieldStatistic result);
    JsonElement Capture();
}

public sealed class FieldAnalyzerContext
{
    public FieldPath Path { get; }
    public CompiledFieldRules? Rules { get; }
    internal FieldAnalyzerContext(string path, CompiledFieldRules? rules) { Path = FieldPath.Parse(path); Rules = rules; }
}

public sealed record AnalyzerDescriptor(string Id, int Version, string ConfigurationKey);

public sealed class FieldAnalyzerStateSnapshot
{
    public string Id { get; set; } = "";
    public JsonElement Data { get; set; }
}
