using AnalysisResult = global::ObjectAnalyzer.AnalysisResult;
using FieldStatistic = global::ObjectAnalyzer.FieldStatistic;

namespace ObjectAnalyzer.Web;

/// <summary>
/// Holds the current analysis result and configuration for the Blazor UI.
/// Registered as scoped service.
/// </summary>
public sealed class AnalysisState
{
    public AnalysisResult? Result { get; set; }
    public string FileName { get; set; } = "";
    public string SortField { get; set; } = "Count";
    public bool SortAscending { get; set; }
    public string SearchText { get; set; } = "";
    public int MaxValues { get; set; } = 5;
    public bool IsLoading { get; set; }
    public string? ErrorMessage { get; set; }

    // 解析选项
    public bool Flatten { get; set; } = true;
    public string ExcelJsonColumn { get; set; } = "A";

    public List<FieldStatistic> GetFilteredSortedFields()
    {
        if (Result == null) return new();

        var fields = Result.FieldStatistics.AsEnumerable();

        // 搜索过滤
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            fields = fields.Where(f =>
                f.FieldName.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
        }

        // 排序
        fields = SortField switch
        {
            "Name" => SortAscending
                ? fields.OrderBy(f => f.FieldName, StringComparer.OrdinalIgnoreCase)
                : fields.OrderByDescending(f => f.FieldName, StringComparer.OrdinalIgnoreCase),
            "Coverage" => SortAscending
                ? fields.OrderBy(f => f.Coverage)
                : fields.OrderByDescending(f => f.Coverage),
            "PrimaryType" => SortAscending
                ? fields.OrderBy(f => f.PrimaryType)
                : fields.OrderByDescending(f => f.PrimaryType),
            _ => SortAscending
                ? fields.OrderBy(f => f.Count)
                : fields.OrderByDescending(f => f.Count)
        };

        return fields.ToList();
    }
}
