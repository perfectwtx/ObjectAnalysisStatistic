using System.Collections.Immutable;

namespace ObjectAnalyzer.Analysis;

/// <summary>Compiles rules evaluated after field states have been merged and projected.</summary>
public interface IResultRuleCompiler
{
    string Id { get; }
    int Version => 1;
    string ConfigurationKey => "";
    CompiledResultRule? Compile(AnalysisRules? definition);
}

public sealed record ResultRuleContext(long TotalObjects, long UniqueObjects, IReadOnlyList<FieldStatistic> Fields);

public sealed class CompiledResultRule
{
    private readonly Func<ResultRuleContext, IEnumerable<FieldViolation>> _evaluate;
    public CompiledResultRule(Func<ResultRuleContext, IEnumerable<FieldViolation>> evaluate)
    {
        ArgumentNullException.ThrowIfNull(evaluate);
        _evaluate = evaluate;
    }
    internal IEnumerable<FieldViolation> Evaluate(ResultRuleContext context) => _evaluate(context);
}

internal static class BuiltInResultRules
{
    public static IEnumerable<IResultRuleCompiler> Create() =>
    [
        new FieldSummaryCompiler("required-null-rate", r => r.Required == true || r.NullRateMax.HasValue,
            QualityChecks.EvaluateNulls),
        new FieldSummaryCompiler("unique", r => r.Unique == true,
            (s, _, _, output) => QualityChecks.EvaluateUnique(s, output)),
        new FieldSummaryCompiler("outlier", r => r.MaxOutlierRatio.HasValue,
            (s, r, _, output) => QualityChecks.EvaluateOutlier(s, r.MaxOutlierRatio!.Value, output)),
        new ExpectationsCompiler()
    ];

    private sealed class FieldSummaryCompiler(string id, Func<FieldRule, bool> enabled,
        Action<FieldStatistic, FieldRule, long, List<FieldViolation>> evaluate) : IResultRuleCompiler
    {
        public string Id => id;
        public CompiledResultRule? Compile(AnalysisRules? definition)
        {
            var fields = definition?.Fields.Where(p => enabled(p.Value))
                .ToImmutableDictionary(p => FieldPath.Parse(p.Key).ToString(), p => p.Value, StringComparer.Ordinal);
            if (fields is null || fields.Count == 0) return null;
            return new(context =>
            {
                var output = new List<FieldViolation>();
                foreach (var field in context.Fields)
                    if (fields.TryGetValue(field.FieldName, out var rule)) evaluate(field, rule, context.TotalObjects, output);
                return output;
            });
        }
    }

    private sealed class ExpectationsCompiler : IResultRuleCompiler
    {
        public string Id => "dataset-expectations";
        public CompiledResultRule? Compile(AnalysisRules? definition)
        {
            if (definition?.Expectations is null) return null;
            return new(context =>
            {
                var output = new List<FieldViolation>();
                QualityChecks.EvaluateExpectations(context.TotalObjects, context.UniqueObjects, definition, output);
                return output;
            });
        }
    }
}
