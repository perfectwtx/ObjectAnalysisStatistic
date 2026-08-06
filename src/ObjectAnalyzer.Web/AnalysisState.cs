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

    // 分页（避免大结果集一次性渲染导致电路消息过大/卡顿）
    public int PageIndex { get; set; }
    public int PageSize { get; set; } = 50;
    public int MaxDuplicatesToShow { get; set; } = 20;

    // 解析选项 (对应 CLI 所有选项)
    public bool Flatten { get; set; } = true;                    // --no-flatten
    public string ExcelJsonColumn { get; set; } = "A";           // --excel-json-column
    public string? Format { get; set; }                           // --format (null=自动检测)
    public int MaxValuesToShow { get; set; } = 5;                // --max-values

    // --type (.cs 类型定义文件)：以临时文件形式保存，避免在内存中缓存大内容
    public string? TypeFilePath { get; set; }
    public string? TypeFileName { get; set; }
    public string? TypeName { get; set; }                         // --type-name

    // 规则文件 (--rules)：以临时文件形式保存
    public string? RulesFilePath { get; set; }
    public string? RulesFileName { get; set; }

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

    /// <summary>当前页的字段（先过滤排序，再分页切片）。</summary>
    public List<FieldStatistic> GetPageFields()
    {
        var all = GetFilteredSortedFields();
        int total = all.Count;
        if (total == 0) return new();
        if (PageIndex > (total - 1) / PageSize)
        {
            PageIndex = (total - 1) / PageSize;
        }
        return all.Skip(PageIndex * PageSize).Take(PageSize).ToList();
    }

    /// <summary>总页数。</summary>
    public int TotalPages
    {
        get
        {
            int total = GetFilteredSortedFields().Count;
            return Math.Max(1, (int)Math.Ceiling(total / (double)PageSize));
        }
    }

    /// <summary>重复对象仅展示前 N 组，避免大结果集。</summary>
    public List<global::ObjectAnalyzer.DuplicateObjectStatistic> GetVisibleDuplicates()
    {
        if (Result == null) return new();
        return Result.DuplicateObjects.Take(MaxDuplicatesToShow).ToList();
    }

    public int HiddenDuplicateCount =>
        Result == null ? 0 : Math.Max(0, Result.DuplicateObjects.Count - MaxDuplicatesToShow);
}
