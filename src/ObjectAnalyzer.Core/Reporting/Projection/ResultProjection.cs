using System.Text.Json;
using ObjectAnalyzer.Data;

namespace ObjectAnalyzer.Reporting;

public sealed record FieldPrivacyDirective(string Field, PiiMaskFormat? Mask, bool Exempt = false);
public sealed record ResultValueView(RecordValueKind Kind, string Display, long Count, bool Redacted);
public sealed class ResultProjectionInfo
{
    public string Contract { get; set; } = "result-delivery/v1";
    public bool Redacted { get; set; }
    public List<string> Omitted { get; set; } = new();
}
public sealed record AnalysisDelivery(AnalysisResult Result, IReadOnlyList<Dictionary<string, object?>> Preview,
    PiiPolicy Policy, bool Redacted);

/// <summary>Single delivery boundary. Computation results remain private and unmodified.</summary>
public static class ResultProjection
{
    public static AnalysisDelivery Create(AnalysisResult result,
        IReadOnlyList<Dictionary<string, object?>>? preview = null, bool redact = true)
    {
        var policy = PiiPolicy.Resolve(result);
        return new(redact ? Redact(result, policy) : AnalysisAccumulator.Clone(result),
            Preview(preview ?? [], redact ? policy : PiiPolicy.Empty), policy, redact);
    }

    public static AnalysisResult? ReadStored(string json)
    {
        var result = JsonSerializer.Deserialize<AnalysisResult>(json);
        return result is null ? null : Redact(result, PiiPolicy.Resolve(result));
    }

    /// <summary>Shared value/type/count view for every host; masking collisions retain the original types.</summary>
    public static IReadOnlyList<ResultValueView> Values(FieldStatistic field)
        => field.ProjectedValues ?? field.ValueCounts.Select(pair => new ResultValueView(pair.Key.Kind,
            pair.Key.ToDisplayString(), pair.Value, false)).OrderByDescending(item => item.Count)
            .ThenBy(item => item.Display, StringComparer.Ordinal).ToList();

    public static AnalysisResult Redact(AnalysisResult source, PiiPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(policy);
        ResultProjectionContract.Validate();
        if (source.GetType() != typeof(AnalysisResult) || source.FieldStatistics.Any(field => field.GetType() != typeof(FieldStatistic)))
            throw new NotSupportedException("Derived result types need an audited projection contract.");
        var result = AnalysisAccumulator.Clone(source);
        var info = result.Projection = new() { Redacted = true };
        void Omit(string carrier) { if (!info.Omitted.Contains(carrier)) info.Omitted.Add(carrier); }
        foreach (var field in result.FieldStatistics)
        {
            var path = FieldPath.Parse(field.FieldName);
            bool sensitive = policy.Affects(path);
            var original = source.FieldStatistics.Single(item => item.FieldName == field.FieldName);
            var values = new Dictionary<RecordValue, long>();
            foreach (var pair in field.ValueCounts)
            {
                var value = Value(pair.Key, path, policy);
                values[value] = values.GetValueOrDefault(value) + pair.Value;
            }
            field.ValueCounts = values;
            field.ProjectedValues = Values(original).Select(item => item with
            {
                Display = ProjectDisplay(item.Display, item.Kind, path, policy), Redacted = item.Redacted || sensitive
            }).GroupBy(item => (item.Kind, item.Display, item.Redacted))
                .Select(group => group.First() with { Count = group.Sum(item => item.Count) })
                .OrderByDescending(item => item.Count).ThenBy(item => item.Display, StringComparer.Ordinal).ToList();
            if (field.DefaultValueRepresentation is { } defaultValue)
                field.DefaultValueRepresentation = ProjectDisplay(defaultValue, RecordValueKind.String, path, policy);
            field.ReplaceEnumViolations(field.EnumViolations.Select(value => ProjectDisplay(value, RecordValueKind.String, path, policy)));
            if (field.ValueRuleResults is not null)
                field.ValueRuleResults = field.ValueRuleResults.ToDictionary(pair => pair.Key, pair => pair.Value with
                {
                    Samples = pair.Value.Samples.Select(value => ProjectDisplay(value, RecordValueKind.String, path, policy)).ToArray(),
                    Message = policy.IsEmpty ? pair.Value.Message : "Rule evaluated; raw message omitted in redacted delivery."
                }, StringComparer.Ordinal);
            // Extension payloads can contain values from other fields; no registered schema means no safe passthrough.
            if (field.AnalyzerResults is not null) { field.AnalyzerResults = null; Omit("analyzer-payloads"); }
            if (field.AppliedRule is { } rule)
            {
                if (rule.Transform is not null) { rule.Transform = null; Omit("transform-configuration"); }
                if (rule.Checks is not null) { rule.Checks = null; Omit("custom-rule-configuration"); }
                if (!policy.IsEmpty)
                {
                    if (sensitive)
                    {
                        rule.DefaultValue = null; rule.EnumValues = null; rule.Pattern = null;
                        rule.MinValue = null; rule.MaxValue = null;
                        Omit("sensitive-rule-literals");
                    }
                }
            }
            if (sensitive)
            {
                field.MinNumber = field.MaxNumber = field.AvgNumber = field.SumNumber = null;
                field.StdDevNumber = field.MedianNumber = field.P90Number = field.P95Number = field.P99Number = null;
                field.Histogram = null; field.MinDateTime = field.MaxDateTime = null; field.DateHistogram = null;
                field.OutlierBounds = null;
                Omit("sensitive-value-summaries");
            }
        }
        result.DuplicateObjects = result.DuplicateObjects.Select(item => new DuplicateObjectStatistic
        { Count = item.Count, ObjectJson = Duplicate(item.ObjectJson, policy) }).ToList();
        result.QualityViolations = result.QualityViolations.Select(item => item with
        {
            Samples = string.IsNullOrEmpty(item.Field) && !policy.IsEmpty ? []
                : item.Samples.Select(value => ProjectDisplay(value, RecordValueKind.String, FieldPath.Parse(item.Field), policy)).ToArray(),
            Message = policy.IsEmpty ? item.Message : "Rule evaluated; raw message omitted in redacted delivery."
        }).ToList();
        if (!policy.IsEmpty)
        {
            if (result.FilterExpression is not null) { result.FilterExpression = null; Omit("filter-literals"); }
            if (result.SchemaDiff is not null) { result.SchemaDiff = null; Omit("schema-diff-values"); }
            if (result.DistributionDiff is not null) { result.DistributionDiff = null; Omit("distribution-diff-values"); }
            if (result.TimeSeriesAnalyses is not null)
                foreach (var key in result.TimeSeriesAnalyses.Keys.Where(key => policy.Affects(FieldPath.Parse(key))).ToArray())
                { result.TimeSeriesAnalyses.Remove(key); Omit("sensitive-time-series"); }
            if (result.StringPatterns is not null)
                foreach (var pair in result.StringPatterns.Where(pair => policy.Affects(FieldPath.Parse(pair.Key))))
                {
                    foreach (var pattern in pair.Value.Patterns.Concat(pair.Value.DominantPattern is null ? [] : [pair.Value.DominantPattern]))
                    {
                        pattern.Template = "[redacted]";
                        pattern.Examples = pattern.Examples.Select(value => ProjectDisplay(value, RecordValueKind.String, FieldPath.Parse(pair.Key), policy)).ToList();
                    }
                }
            if (result.FuzzyDuplicates is { } fuzzy)
            {
                if (fuzzy.Field is null) { result.FuzzyDuplicates = null; Omit("fuzzy-values-without-field"); }
                else if (policy.Affects(FieldPath.Parse(fuzzy.Field)))
                    foreach (var group in fuzzy.Groups)
                    {
                        var path = FieldPath.Parse(fuzzy.Field);
                        group.CanonicalKey = ProjectDisplay(group.CanonicalKey, RecordValueKind.String, path, policy);
                        group.RepresentativeJson = ProjectDisplay(group.RepresentativeJson, RecordValueKind.String, path, policy);
                        group.Examples = group.Examples.Select(value => ProjectDisplay(value, RecordValueKind.String, path, policy)).ToList();
                    }
            }
        }
        // Keep omission evidence stable when projecting a previously projected result.
        info.Omitted = info.Omitted.Concat(source.Projection?.Omitted ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        return result;
    }

    public static IReadOnlyList<Dictionary<string, object?>> Preview(
        IReadOnlyList<Dictionary<string, object?>> rows, PiiPolicy policy)
        => rows.Select(row => row.ToDictionary(pair => pair.Key,
            pair => Value(RecordValue.FromObject(pair.Value), FieldPath.Parse(pair.Key), policy).ToObject(), StringComparer.Ordinal)).ToList();

    internal static RecordValue Value(RecordValue value, FieldPath path, PiiPolicy policy)
    {
        if (value.Kind == RecordValueKind.Object)
            return RecordValue.FromObject(value.Properties.ToDictionary(pair => pair.Key,
                pair => Value(pair.Value, path.AppendProperty(pair.Key), policy), StringComparer.Ordinal));
        if (value.Kind == RecordValueKind.Array)
            return RecordValue.FromObject(value.Items.Select((item, index) => Value(item, path.AppendIndex(index), policy)).ToList());
        var mask = policy.Find(path);
        return mask is null || value.Kind == RecordValueKind.Null ? value : RecordValue.FromString(mask.Apply(value.ToDisplayString()));
    }

    private static string ProjectDisplay(string text, RecordValueKind kind, FieldPath path, PiiPolicy policy)
    {
        if (!policy.Affects(path)) return text;
        // Containers may be serialized display values. Never return an unparsed container when descendants are sensitive.
        if (kind is RecordValueKind.Object or RecordValueKind.Array || policy.Find(path) is null)
        {
            try { using var document = JsonDocument.Parse(text); return Value(RecordValue.FromJsonElement(document.RootElement), path, policy).ToDisplayString(); }
            catch (JsonException) { return "[redacted]"; }
        }
        return policy.Find(path)!.Apply(text);
    }

    private static string Duplicate(string text, PiiPolicy policy)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return policy.IsEmpty ? text : "[redacted]";
            var projected = Preview([document.RootElement.EnumerateObject().ToDictionary(pair => pair.Name,
                pair => (object?)RecordValue.FromJsonElement(pair.Value), StringComparer.Ordinal)], policy);
            return JsonSerializer.Serialize(projected[0]);
        }
        catch (JsonException) { return policy.IsEmpty ? text : "[redacted]"; }
    }
}
