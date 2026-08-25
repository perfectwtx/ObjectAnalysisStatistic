using System.IO;
using AnalysisResult = global::ObjectAnalyzer.Analysis.AnalysisResult;
using FieldStatistic = global::ObjectAnalyzer.Analysis.FieldStatistic;

namespace ObjectAnalyzer.Web;

/// <summary>
/// Holds the current analysis result and configuration for the Blazor UI.
/// Registered as scoped service.
/// </summary>
public sealed class AnalysisState : IDisposable
{
    private static readonly System.Text.Json.JsonSerializerOptions ViolationsJsonOptions =
        new() { WriteIndented = true };

    public AnalysisResult? Result { get; set; }

    /// <summary>解析结果的前 N 行预览（用于展示数据长什么样）。</summary>
    public List<Dictionary<string, object?>>? Preview { get; set; }

    public string FileName { get; set; } = "";
    public string SortField { get; set; } = "Count";
    public bool SortAscending { get; set; }
    public string SearchText { get; set; } = "";
    public bool IsLoading { get; set; }
    public string? ErrorMessage { get; set; }

    // 已上传的数据文件（临时路径）；上传后等待用户点击"开始分析"
    public string? DataFilePath { get; set; }

    public bool HasData => DataFilePath != null;

    // 分页（避免大结果集一次性渲染导致电路消息过大/卡顿）
    public int PageIndex { get; set; }
    public int PageSize { get; set; } = 50;
    public int MaxDuplicatesToShow { get; set; } = 20;

    // 解析选项 (对应 CLI 所有选项)
    public bool Flatten { get; set; } = true;                    // --no-flatten
    public int MaxValuesToShow { get; set; } = 5;                // --max-values
    public int MaxDegreeOfParallelism { get; set; } = -1;        // --parallel (-1 = 全部核心)
    public int SampleReservoir { get; set; }                     // --sample-reservoir (>0 启用)
    public int SampleStep { get; set; }                          // --sample-step (>1 启用)

    // --type (.cs 类型定义文件)：以临时文件形式保存，避免在内存中缓存大内容
    public string? TypeFilePath { get; set; }
    public string? TypeFileName { get; set; }
    public string? TypeName { get; set; }                         // --type "File.cs:ClassName" 冒号后部分

    // 规则文件 (--rules)：以临时文件形式保存
    public string? RulesFilePath { get; set; }
    public string? RulesFileName { get; set; }

    // ── P3 Schema 快照基线（--diff-schema）──
    // 用户上传的 schema snapshot JSON，作为 diff 的 baseline。基线独立于本次分析的源数据。
    public string? BaselineSnapshotPath { get; set; }
    public string? BaselineSnapshotName { get; set; }

    /// <summary>用户上传基线后，本地计算（不重新跑分析）的 SchemaDiff 副本；未上传基线时为 null。</summary>
    public global::ObjectAnalyzer.Analysis.SchemaDiff? ComputedSchemaDiff { get; set; }

    /// <summary>基线 JSON 解析/校验失败时的错误消息（展示给用户）。</summary>
    public string? BaselineError { get; set; }

    // --fields：仅统计这些字段（逗号分隔）
    public string Fields { get; set; } = "";

    // --where：按字段值过滤对象后再统计（过滤表达式）
    public string Filter { get; set; } = "";

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
            "Semantic" => SortAscending
                ? fields.OrderBy(f => f.SemanticType)
                : fields.OrderByDescending(f => f.SemanticType),
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
    public List<global::ObjectAnalyzer.Analysis.DuplicateObjectStatistic> GetVisibleDuplicates()
    {
        if (Result == null) return new();
        return Result.DuplicateObjects.Take(MaxDuplicatesToShow).ToList();
    }

    public long HiddenDuplicateCount =>
        Result == null ? 0L : Math.Max(0L, Result.TotalDuplicateGroups - MaxDuplicatesToShow);

    /// <summary>枚举违规的结构化 JSON（与 CLI --violations 输出一致），用于下载。</summary>
    public string GetViolationsJson()
    {
        if (Result == null) return "[]";
        var violations = Result.FieldStatistics
            .Where(f => f.EnumViolations.Count > 0)
            .Select(f => new
            {
                field = f.FieldName,
                values = f.EnumViolations
            })
            .ToList();
        return System.Text.Json.JsonSerializer.Serialize(violations, ViolationsJsonOptions);
    }

    /// <summary>电路断开时清理所有上传的临时文件，避免 %TEMP% 残留。</summary>
    public void Dispose()
    {
        DeleteQuietly(DataFilePath);
        DeleteQuietly(TypeFilePath);
        DeleteQuietly(RulesFilePath);
        DeleteQuietly(BaselineSnapshotPath);
    }

    private static void DeleteQuietly(string? path)
    {
        if (path == null) return;
        try { File.Delete(path); } catch { }
    }
}
