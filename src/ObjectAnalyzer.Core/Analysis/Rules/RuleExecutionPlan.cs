using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ObjectAnalyzer.Analysis;

/// <summary>Stateless compiler. Compile once per configured field, before input is enumerated.</summary>
public interface IValueRuleCompiler
{
    string Id { get; }
    int Version => 1;
    string ConfigurationKey => "";
    CompiledValueRule? Compile(FieldPath path, FieldRule definition);
}

/// <summary>Acceptance predicates must be pure and thread-safe; counters live in per-worker states.</summary>
public sealed class CompiledValueRule
{
    public string Id { get; }
    public string Message { get; }
    private readonly Func<RecordValue, bool> _accepts;
    private readonly bool _skipNull;
    public CompiledValueRule(string id, string message, Func<RecordValue, bool> accepts, bool skipNull = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(accepts);
        Id = id; Message = message; _accepts = accepts; _skipNull = skipNull;
    }
    internal bool IsViolation(RecordValue value) => !(_skipNull && value.Kind == RecordValueKind.Null) && !_accepts(value);
}

public sealed class CompiledFieldRules
{
    private readonly FieldRule _definition;
    public IReadOnlyList<CompiledValueRule> Checks { get; }
    public FieldRule GetDefinition() => AnalysisAccumulator.Clone(_definition);
    internal bool RequiresPresenceSummary => _definition.Required == true || _definition.NullRateMax.HasValue;
    internal CompiledFieldRules(FieldRule definition, IEnumerable<CompiledValueRule> checks)
    {
        _definition = AnalysisAccumulator.Clone(definition);
        Checks = checks.ToImmutableArray();
    }
}

/// <summary>Immutable plan with canonical paths and per-run regex/enum ownership.</summary>
public sealed class RuleExecutionPlan
{
    private readonly FrozenDictionary<string, CompiledFieldRules> _fields;
    private readonly ImmutableArray<CompiledResultRule> _resultChecks;
    internal RuleExecutionPlan(AnalysisRules? rules, FieldAnalyzerRegistry registry)
    {
        var fields = new Dictionary<string, CompiledFieldRules>(StringComparer.Ordinal);
        foreach (var pair in rules?.Fields ?? new())
        {
            var path = FieldPath.Parse(pair.Key);
            var definition = pair.Value;
            foreach (string id in definition.Checks?.Keys ?? Enumerable.Empty<string>())
                if (!registry.RuleCompilers.Any(c => c.Id == id)) throw new ArgumentException($"Unknown value rule '{id}' on '{path}'.");
            var compiled = new List<CompiledValueRule>();
            foreach (var compiler in registry.RuleCompilers)
            {
                var rule = compiler.Compile(path, AnalysisAccumulator.Clone(definition));
                if (rule is not null)
                {
                    if (rule.Id != compiler.Id) throw new ArgumentException("Compiled rule ID must match its registration.");
                    compiled.Add(rule);
                }
                else if (definition.Checks?.ContainsKey(compiler.Id) == true)
                    throw new ArgumentException($"Value rule '{compiler.Id}' was configured but not compiled.");
            }
            if (compiled.Count > 0 && !registry.Contains(BuiltInAnalyzers.RulesId))
                throw new ArgumentException("Value rules require the value-rules analyzer.");
            if (definition.Unique == true && !registry.Contains(BuiltInAnalyzers.DistinctId))
                throw new ArgumentException("Unique rules require the distinct analyzer.");
            if (definition.MaxOutlierRatio.HasValue && !registry.Contains(BuiltInAnalyzers.NumericId))
                throw new ArgumentException("Outlier rules require the numeric analyzer.");
            fields.Add(path.ToString(), new CompiledFieldRules(definition, compiled));
        }
        _fields = fields.ToFrozenDictionary(StringComparer.Ordinal);
        _resultChecks = registry.ResultRuleCompilers.Select(c => c.Compile(rules is null ? null : AnalysisAccumulator.Clone(rules)))
            .OfType<CompiledResultRule>().ToImmutableArray();
    }

    public CompiledFieldRules? Find(FieldPath path) => Find(path.ToString());
    internal CompiledFieldRules? Find(string path) => _fields.GetValueOrDefault(path);
    internal IEnumerable<string> Paths => _fields.Where(p => p.Value.RequiresPresenceSummary).Select(p => p.Key);
    internal IEnumerable<FieldViolation> EvaluateResults(ResultRuleContext context) => _resultChecks.SelectMany(r => r.Evaluate(context));
}

internal static class BuiltInValueRules
{
    public static IEnumerable<IValueRuleCompiler> Create() => [new EnumCompiler(), new RangeCompiler(), new PatternCompiler()];

    private sealed class EnumCompiler : IValueRuleCompiler
    {
        public string Id => QualityCheckTypes.EnumValues;
        public CompiledValueRule? Compile(FieldPath path, FieldRule definition)
        {
            if (definition.EnumValues is not { Count: > 0 }) return null;
            var allowed = definition.EnumValues.Select(RecordValue.FromJsonElement).ToFrozenSet();
            return new(Id, "值不在枚举范围内", allowed.Contains, skipNull: false);
        }
    }
    private sealed class RangeCompiler : IValueRuleCompiler
    {
        public string Id => QualityCheckTypes.Range;
        public CompiledValueRule? Compile(FieldPath path, FieldRule definition)
        {
            if (!definition.MinValue.HasValue && !definition.MaxValue.HasValue) return null;
            double min = definition.MinValue ?? double.NegativeInfinity, max = definition.MaxValue ?? double.PositiveInfinity;
            if (min > max || double.IsNaN(min) || double.IsNaN(max)) throw new ArgumentException($"Invalid numeric bounds on '{path}'.");
            return new(Id, string.Create(CultureInfo.InvariantCulture, $"值超出范围 [{min:G6}, {max:G6}]"),
                value => !value.TryGetDouble(out double number) || number >= min && number <= max);
        }
    }
    private sealed class PatternCompiler : IValueRuleCompiler
    {
        public string Id => QualityCheckTypes.Pattern;
        public CompiledValueRule? Compile(FieldPath path, FieldRule definition)
        {
            if (string.IsNullOrEmpty(definition.Pattern)) return null;
            Regex regex = QualityChecks.CompilePattern(definition.Pattern);
            return new(Id, $"值不匹配模式 /{definition.Pattern}/", value => regex.IsMatch(value.ToDisplayString()));
        }
    }
}
