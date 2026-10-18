using System.Collections.Frozen;
using System.Reflection;
using System.Text.Json.Serialization;

namespace ObjectAnalyzer.Reporting;

/// <summary>Audited member inventory. New members require an explicit delivery decision before any projection succeeds.</summary>
public static class ResultProjectionContract
{
    public static IReadOnlyDictionary<Type, string> Members { get; } = new Dictionary<Type, string>
    {
        [typeof(AnalysisResult)] = "PrivacyDirectives Projection Reliability TotalObjects TotalUniqueObjects TotalDuplicateGroups FilterExpression DuplicateObjects FieldStatistics QualityViolations ObjectRelationships SchemaDiff CorrelationMatrix FuzzyDuplicates StringPatterns UnicodeHygiene TimeSeriesAnalyses DistributionDiff SamplingConfidence",
        [typeof(FieldStatistic)] = "FieldName Path ProjectedValues Count Coverage NullCount TypeCounts ValueCounts OtherValueCount NumbersAreIntegral NumbersFitInt64 NumbersFitDecimal NumbersFitDouble ValueCountsTruncated MinNumber MaxNumber AvgNumber SumNumber StdDevNumber MedianNumber P90Number P95Number P99Number Histogram DateTimeCount MinDateTime MaxDateTime DateHistogram OutlierMethod OutlierCount OutlierRatio OutlierBounds MinLength MaxLength AvgLength DistinctCount DistinctApproximate SemanticType PrimaryType NullAppeared DefaultValueRepresentation AnalyzerResults ValueRuleResults EnumViolationCount AppliedRule TransformFailureCount TransformType RangeViolationCount EnumViolations _enumViolations",
        [typeof(FieldRule)] = "Checks DefaultValue PrimaryType EnumValues Transform Pii Redact MinValue MaxValue Required Pattern Unique NullRateMax MaxOutlierRatio HasQualityChecks",
        [typeof(FieldViolation)] = "Field Check Count Message Approximate DistinctValueCount DistinctApproximate Samples",
        [typeof(ValueRuleResult)] = "SamplesTruncated Count DistinctValueCount DistinctApproximate Message Samples",
        [typeof(FuzzyDuplicateReport)] = "Field TotalSamples GroupCount SimilarDuplicates Groups",
        [typeof(FuzzyDuplicateGroup)] = "RepresentativeJson CanonicalKey Count AverageSimilarity Examples",
        [typeof(StringPatternProfile)] = "TotalSamples DominantPattern Patterns OtherCount",
        [typeof(StringPatternTemplate)] = "Template Count Ratio Examples",
        [typeof(DuplicateObjectStatistic)] = "Count ObjectJson",
        [typeof(FieldPrivacyDirective)] = "Field Mask Exempt",
        [typeof(ResultValueView)] = "Kind Display Count Redacted",
        [typeof(ResultProjectionInfo)] = "Contract Redacted Omitted"
    }.ToFrozenDictionary();
    /// <summary>Input-bearing locations and the handler decisions that must be revisited when the schema evolves.</summary>
    public static IReadOnlyDictionary<Type, string> ValueCarriers { get; } = new Dictionary<Type, string>
    {
        [typeof(AnalysisResult)] = "DuplicateObjects FieldStatistics QualityViolations FuzzyDuplicates StringPatterns FilterExpression SchemaDiff DistributionDiff TimeSeriesAnalyses",
        [typeof(FieldStatistic)] = "ValueCounts ProjectedValues DefaultValueRepresentation EnumViolations AnalyzerResults ValueRuleResults AppliedRule MinNumber MaxNumber AvgNumber SumNumber StdDevNumber MedianNumber P90Number P95Number P99Number Histogram MinDateTime MaxDateTime DateHistogram OutlierBounds",
        [typeof(FieldRule)] = "DefaultValue EnumValues Pattern MinValue MaxValue Transform Checks",
        [typeof(FieldViolation)] = "Samples Message",
        [typeof(ValueRuleResult)] = "Samples Message",
        [typeof(DuplicateObjectStatistic)] = "ObjectJson",
        [typeof(FuzzyDuplicateGroup)] = "RepresentativeJson CanonicalKey Examples",
        [typeof(StringPatternTemplate)] = "Template Examples"
    }.ToFrozenDictionary();

    private static readonly Lazy<bool> Audited = new(() =>
    {
        foreach (var pair in Members) ValidateMembers(pair.Key, pair.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        foreach (var pair in ValueCarriers)
            if (pair.Value.Split(' ').Any(member => !Members[pair.Key].Split(' ').Contains(member)))
                throw new InvalidOperationException("A value carrier is missing from the audited member inventory.");
        return true;
    });
    public static void Validate() => _ = Audited.Value;
    public static void ValidateMembers(Type type, IEnumerable<string> auditedMembers)
    {
        var actual = type.GetProperties(BindingFlags.Instance | BindingFlags.Public).Select(property => property.Name)
            .Concat(type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(member => member.IsPublic || member.GetCustomAttribute<JsonIncludeAttribute>() is not null).Select(member => member.Name))
            .Order(StringComparer.Ordinal);
        var expected = auditedMembers.Order(StringComparer.Ordinal);
        if (!actual.SequenceEqual(expected))
            throw new InvalidOperationException($"Delivery members of {type.Name} changed; audit ResultProjectionContract and its value handlers before exporting.");
    }
}
