using System.Collections.Immutable;

namespace ObjectAnalyzer.Analysis;

/// <summary>Per-analysis immutable registration; no process-wide mutable plugin registry.</summary>
public sealed class FieldAnalyzerRegistry
{
    internal ImmutableArray<IFieldAnalyzer> Factories { get; }
    public IReadOnlyList<AnalyzerDescriptor> Descriptors { get; }
    internal ImmutableArray<IValueRuleCompiler> RuleCompilers { get; }
    internal ImmutableArray<IResultRuleCompiler> ResultRuleCompilers { get; }
    public string RuleConfigurationKey { get; }
    public static FieldAnalyzerRegistry Default { get; } = new(BuiltInAnalyzers.Create(), BuiltInValueRules.Create(), BuiltInResultRules.Create());

    private FieldAnalyzerRegistry(IEnumerable<IFieldAnalyzer> factories, IEnumerable<IValueRuleCompiler> ruleCompilers,
        IEnumerable<IResultRuleCompiler> resultRuleCompilers)
    {
        Factories = factories.OrderBy(f => f.Id, StringComparer.Ordinal).ToImmutableArray();
        RuleCompilers = ruleCompilers.OrderBy(r => r.Id, StringComparer.Ordinal).ToImmutableArray();
        ResultRuleCompilers = resultRuleCompilers.OrderBy(r => r.Id, StringComparer.Ordinal).ToImmutableArray();
        if (Factories.Any(f => string.IsNullOrWhiteSpace(f.Id) || f.Version <= 0)
            || Factories.Select(f => f.Id).Distinct(StringComparer.Ordinal).Count() != Factories.Length
            || RuleCompilers.Any(r => string.IsNullOrWhiteSpace(r.Id) || r.Version <= 0)
            || RuleCompilers.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count() != RuleCompilers.Length
            || ResultRuleCompilers.Any(r => string.IsNullOrWhiteSpace(r.Id) || r.Version <= 0)
            || ResultRuleCompilers.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count() != ResultRuleCompilers.Length)
            throw new ArgumentException("Analyzer and rule registrations must have unique, nonempty IDs and positive versions.");
        RuleConfigurationKey = RecordValue.FromJsonElement(System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            Value = RuleCompilers.Select(r => new { r.Id, r.Version, r.ConfigurationKey }),
            Result = ResultRuleCompilers.Select(r => new { r.Id, r.Version, r.ConfigurationKey })
        })).CanonicalKey;
        Descriptors = Factories.Select(f => new AnalyzerDescriptor(f.Id, f.Version,
            f.Id == BuiltInAnalyzers.RulesId
                ? RecordValue.FromJsonElement(System.Text.Json.JsonSerializer.SerializeToElement(RuleCompilers.Select(r => new { r.Id, r.Version, r.ConfigurationKey }))).CanonicalKey : f.ConfigurationKey)).ToImmutableArray();
    }

    public FieldAnalyzerRegistry With(IFieldAnalyzer analyzer)
    {
        ArgumentNullException.ThrowIfNull(analyzer);
        return new(Factories.Append(analyzer), RuleCompilers, ResultRuleCompilers);
    }

    public FieldAnalyzerRegistry Without(string id) => new(Factories.Where(f => f.Id != id), RuleCompilers, ResultRuleCompilers);

    public FieldAnalyzerRegistry WithRule(IValueRuleCompiler compiler)
    {
        ArgumentNullException.ThrowIfNull(compiler);
        return new(Factories, RuleCompilers.Append(compiler), ResultRuleCompilers);
    }

    public FieldAnalyzerRegistry WithResultRule(IResultRuleCompiler compiler)
    {
        ArgumentNullException.ThrowIfNull(compiler);
        return new(Factories, RuleCompilers, ResultRuleCompilers.Append(compiler));
    }

    internal bool Contains(string id) => Factories.Any(f => f.Id == id);

    internal FieldAnalyzerRegistry Resolve(IReadOnlyList<AnalyzerDescriptor> descriptors)
    {
        if (descriptors.Select(d => d.Id).Distinct(StringComparer.Ordinal).Count() != descriptors.Count)
            throw new InvalidDataException("Duplicate analyzer descriptors.");
        foreach (var descriptor in descriptors)
            if (!Descriptors.Contains(descriptor)) throw new InvalidDataException($"Analyzer '{descriptor.Id}' is missing or incompatible. Register its matching implementation before restoring.");
        return new(Factories.Where(f => descriptors.Any(d => d.Id == f.Id)), RuleCompilers, ResultRuleCompilers);
    }
}
