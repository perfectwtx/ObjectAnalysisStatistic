using System.Collections.Frozen;
using ObjectAnalyzer.Analysis;

namespace ObjectAnalyzer.Platform;

public sealed record QualityIssueSearch(Guid? JobId = null, string? Severity = null,
    string? Check = null, string? Status = null, int Offset = 0, int Limit = 200);

public sealed record QualityIssueSearchItem(QualityIssue Issue,
    QualityIssueResponsibility Responsibility, DateTime StartedAtUtc);

public sealed record QualityIssueSearchPage(IReadOnlyList<QualityIssueSearchItem> Items, int Total);

public sealed record QualityIssueSummary(int TotalIssues, long TotalViolations,
    IReadOnlyDictionary<string, int> BySeverity, IReadOnlyDictionary<string, int> ByCheck,
    IReadOnlyDictionary<string, int> ByField, IReadOnlyDictionary<string, int> ByStatus);

/// <summary>Shared severity classification for query filters and delivery.</summary>
public static class QualityIssueSeverity
{
    public static IReadOnlyDictionary<string, string> ByCheck { get; } = new Dictionary<string, string>
    {
        [QualityCheckTypes.Unique] = "critical", [QualityCheckTypes.DuplicateGroups] = "critical",
        [QualityCheckTypes.Required] = "high", [QualityCheckTypes.Range] = "high",
        [QualityCheckTypes.Pattern] = "high", [QualityCheckTypes.EnumValues] = "high",
        [QualityCheckTypes.NullRate] = "medium", [QualityCheckTypes.RowCount] = "medium",
        [QualityCheckTypes.DuplicateRate] = "medium", [QualityCheckTypes.OutlierRatio] = "medium"
    }.ToFrozenDictionary(StringComparer.Ordinal);

    public static string Get(string check) => ByCheck.TryGetValue(check, out var severity) ? severity : "low";
}
