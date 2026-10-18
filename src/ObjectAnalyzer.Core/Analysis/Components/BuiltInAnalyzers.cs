using System.Text.Json;

namespace ObjectAnalyzer.Analysis;

internal static partial class BuiltInAnalyzers
{
    private static readonly JsonSerializerOptions StateOptions = new() { MaxDepth = 512 };
    public const string NumericId = "numeric";
    public const string TextId = "text";
    public const string DateId = "date";
    public const string DistinctId = "distinct";
    public const string RulesId = "value-rules";
    internal static readonly string[] MetricIds = [NumericId, TextId, DateId, DistinctId];

    public static IEnumerable<IFieldAnalyzer> Create() =>
    [
        new MetricsAnalyzer(NumericId, [RecordValueKind.Number],
            (m, v, _) => { if (v.TryGetDouble(out double number)) m.AddNumber(number); }, CompleteNumeric),
        new MetricsAnalyzer(TextId, [RecordValueKind.String], ObserveText, CompleteText),
        new MetricsAnalyzer(DateId, [RecordValueKind.String, RecordValueKind.Date, RecordValueKind.DateTime, RecordValueKind.DateTimeOffset],
            (m, v, _) =>
            {
                if ((v.Kind != RecordValueKind.String || RegexDateTime.IsMatch(v.Scalar)) && TryParseDateTimeOffset(v.Scalar, out var date)) m.AddDateTime(date);
            }, CompleteDate),
        new MetricsAnalyzer(DistinctId, Enum.GetValues<RecordValueKind>(), (m, v, _) => m.AddDistinct(v.CanonicalKey),
            (m, result) =>
            {
                if (!result.ValueCountsTruncated) result.DistinctCount = result.ValueCounts.Count;
                else if (m.DistinctHll is not null) { result.DistinctCount = m.DistinctHll.Estimate(); result.DistinctApproximate = true; }
            }),
        new ValueRulesAnalyzer()
    ];

    private sealed class MetricsAnalyzer(string id, IReadOnlyCollection<RecordValueKind> kinds,
        Action<InternalFieldMetrics, RecordValue, string> observe, Action<InternalFieldMetrics, FieldStatistic> complete) : IFieldAnalyzer
    {
        public string Id => id;
        public int Version => 1;
        public IReadOnlyCollection<RecordValueKind> InputKinds => kinds;
        public IFieldAnalyzerState CreateState(FieldAnalyzerContext context) => new MetricsState(new(context.ResourceLimits.MaxTimeBins), context.Path.ToString(), observe, complete);
        public IFieldAnalyzerState RestoreState(FieldAnalyzerContext context, JsonElement data)
            => new MetricsState(InternalFieldMetrics.Restore(data.Deserialize<FieldMetricsSnapshot>() ?? throw new InvalidDataException("Missing metric state."), context.ResourceLimits.MaxTimeBins),
                context.Path.ToString(), observe, complete);
    }

    private sealed class MetricsState(InternalFieldMetrics metrics, string path,
        Action<InternalFieldMetrics, RecordValue, string> observe, Action<InternalFieldMetrics, FieldStatistic> complete) : IFieldAnalyzerState, IFieldMetricsProvider
    {
        public InternalFieldMetrics Metrics => metrics;
        public void Observe(RecordValue value) => observe(metrics, value, path);
        public void Merge(IFieldAnalyzerState other) => metrics.MergeFrom(((MetricsState)other).Metrics);
        public void Complete(FieldStatistic result) => complete(metrics, result);
        public JsonElement Capture() => JsonSerializer.SerializeToElement(metrics.Capture());
    }

    private sealed class ValueRulesAnalyzer : IFieldAnalyzer
    {
        public string Id => RulesId;
        public int Version => 1;
        public IReadOnlyCollection<RecordValueKind> InputKinds { get; } = Enum.GetValues<RecordValueKind>();
        public bool IsEnabled(FieldAnalyzerContext context) => context.Rules?.Checks.Count > 0;
        public IFieldAnalyzerState CreateState(FieldAnalyzerContext context) => new ValueRulesState(context.Rules!.Checks, context.ResourceLimits, context.Payload);
        public IFieldAnalyzerState RestoreState(FieldAnalyzerContext context, JsonElement data)
        {
            var state = new ValueRulesState(context.Rules!.Checks, context.ResourceLimits, context.Payload);
            var saved = data.Deserialize<Dictionary<string, ValueRuleStateSnapshot>>(StateOptions) ?? throw new InvalidDataException("Missing rule state.");
            if (!saved.Keys.Order(StringComparer.Ordinal).SequenceEqual(context.Rules.Checks.Select(c => c.Id).Order(StringComparer.Ordinal)))
                throw new InvalidDataException("Rule state does not match the compiled plan.");
            foreach (var p in saved) state.Values[p.Key] = ValueRuleAccumulator.Restore(p.Value, context.ResourceLimits, context.Payload);
            return state;
        }
    }

    private sealed class ValueRulesState(IReadOnlyList<CompiledValueRule> rules, AnalysisResourceLimits limits, RetainedPayloadBudget? payload) : IFieldAnalyzerState
    {
        public Dictionary<string, ValueRuleAccumulator> Values { get; } = rules.ToDictionary(r => r.Id, _ => new ValueRuleAccumulator(limits, payload), StringComparer.Ordinal);
        public void Observe(RecordValue value)
        {
            foreach (var rule in rules) if (rule.IsViolation(value)) Values[rule.Id].Add(value);
        }
        public void Merge(IFieldAnalyzerState other)
        {
            foreach (var p in ((ValueRulesState)other).Values) Values[p.Key].Merge(p.Value);
        }
        public void Complete(FieldStatistic result)
        {
            result.ValueRuleResults = rules.ToDictionary(r => r.Id, r => Values[r.Id].Complete(r.Message), StringComparer.Ordinal);
            result.RangeViolationCount = result.ValueRuleResults.GetValueOrDefault(QualityCheckTypes.Range)?.Count ?? 0;
            var enums = result.ValueRuleResults.GetValueOrDefault(QualityCheckTypes.EnumValues);
            result.EnumViolationCount = enums?.Count ?? 0;
            if (enums is not null) foreach (string sample in enums.Samples) result.AddEnumViolation(sample);
        }
        public JsonElement Capture() => JsonSerializer.SerializeToElement(Values.ToDictionary(p => p.Key, p => p.Value.Capture()), StateOptions);
    }
}
