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

    /// <summary>CSV 数字列推断为数值类型（--csv-infer-numbers / --no-csv-number-inference）。
    /// 默认 true：30 → 30、3.14 → 3.14；前导零 / 长数字 / 科学计数法 / 带单位保持字符串避免误转。
    /// 关掉后整列按字符串统计，失去数值剖析（均值/分位数/相关性等）。</summary>
    public bool CsvInferNumbers { get; set; } = true;

    /// <summary>规则文件里出现不认识的键时是否放行（--allow-unknown-rules）。
    /// 默认 false：拼错/已废弃的键会被 RulesLint 拦下并返回 400，避免规则看似生效实则被静默忽略；
    /// 确认可忽略时勾选此项放行。</summary>
    public bool AllowUnknownRules { get; set; }

    // ── P4 深度分析开关（默认全关，与 v1.x 行为一致；开启后填充"深度分析"选项卡）──
    public bool EnableCorrelation { get; set; }                  // --correlation
    public bool EnableStringPatterns { get; set; }               // --string-patterns
    public bool EnableUnicodeHygiene { get; set; }               // --unicode-hygiene
    public bool EnableTimeSeries { get; set; }                   // --time-series
    public bool EnableDistributionSnapshot { get; set; }         // --distribution-snapshot

    /// <summary>任一 P4 开关被勾选（用于在 UI 标识需要走 AdvancedAnalysisOptions 路径）。</summary>
    public bool HasAnyP4Switch =>
        EnableCorrelation || EnableStringPatterns
        || EnableUnicodeHygiene || EnableTimeSeries
        || EnableDistributionSnapshot;

    // --type (.cs 类型定义文件)：以临时文件形式保存，避免在内存中缓存大内容

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

    // ── JSONPath 投影（仅分析指定节点下的值）──
    /// <summary>--root-path：在文件根应用 JSONPath 表达式，从整体文件中提取子节点作为分析输入。
    /// 仅对整体 JSON 文件生效；JSONL/CSV/Excel 等流式格式会被忽略并提示。</summary>
    public string RootPath { get; set; } = "";

    /// <summary>--record-path：在每条记录上应用 JSONPath 表达式，提取子节点替换为新记录后再分析。
    /// 对 JSON / JSONL / CSV / Excel / DB 均生效。</summary>
    public string RecordPath { get; set; } = "";

    /// <summary>是否任一 JSONPath 表达式被设置（用于 UI 提示）。</summary>
    public bool HasJsonPath => !string.IsNullOrWhiteSpace(RootPath) || !string.IsNullOrWhiteSpace(RecordPath);

    // ── 数据血缘与审计日志（--audit-log / --operator）──
    /// <summary>
    /// Web UI 用户填写的审计日志输出路径（--audit-log）。当非空时，分析完成后会向该文件追加
    /// 一条 NDJSON 事件（analysis_finish，含 inputHash / operator / 选项摘要 / 结果摘要 / 退出码）。
    /// </summary>
    public string AuditLogPath { get; set; } = "";

    /// <summary>
    /// 操作者标识（--operator）。推荐填入邮箱或 CI runner ID；缺省 "anonymous"。
    /// </summary>
    public string Operator { get; set; } = "anonymous";

    public bool HasAuditLog => !string.IsNullOrWhiteSpace(AuditLogPath);

    // ── P6.1 进度条 / ETA ──
    /// <summary>
    /// 是否在分析过程中输出进度信息（--no-progress 取反，默认 true）。
    /// Web UI 当前只在后端分析结束时才反馈给浏览器，期间浏览器看不到进度（属于
    /// 当前实现的限制；此处保留字段以与 AnalysisOptions 对齐，便于未来接入
    /// SignalR 实时进度推送）。
    /// </summary>
    public bool ShowProgress { get; set; } = true;

    // ── P6.2 增量分析 / 断点续跑 / 基线合并 ──
    // --checkpoint：每处理完一个分块后落盘 AnalyzerSnapshot；以临时文件形式保存上传的检查点目标路径。
    public string? CheckpointPath { get; set; }
    public string? CheckpointFileName { get; set; }

    // --resume：从历史 AnalyzerSnapshot 恢复；以临时文件形式保存用户上传的检查点。
    public string? ResumeFrom { get; set; }
    public string? ResumeFileName { get; set; }

    // --baseline：与历史 AnalysisResult JSON 合并；以临时文件形式保存用户上传的基线。
    public string? BaselinePath { get; set; }
    public string? BaselineFileName { get; set; }

    // ── P6.3 内存预算（--max-memory）──
    /// <summary>进程托管堆上限（MB，0 = 不监控）。</summary>
    public long MaxMemoryMB { get; set; }

    /// <summary>超限行为：soft（警告 + 自动采样）/ hard（立即失败）。</summary>
    public string MaxMemoryMode { get; set; } = "soft";

    // ── P6 运行时状态（由分析循环填充，UI 只读展示）──
    /// <summary>
    /// 当前进度文本（IProgress&lt;long&gt; 回调更新）。形如 "已分析 12,345 条对象"。
    /// 仅在 <see cref="ShowProgress"/> = true 时非空；分析结束后被清空。
    /// </summary>
    public string ProgressText { get; set; } = "";

    /// <summary>
    /// 最近一次分析产出的检查点文件服务器路径（%TEMP%/objectanalyzer-checkpoints/&lt;name&gt;.snapshot）。
    /// 与 <see cref="CheckpointFileName"/> 不同：后者是用户在 UI 上输入的文件名（模板），前者是实际写入位置。
    /// UI 可基于此字段提供「下载检查点」按钮。
    /// </summary>
    public string? LastCheckpointServerPath { get; set; }

    /// <summary>最近一次检查点字节大小（用于展示给用户「检查点已生成（12.3 MB）」）。</summary>
    public long LastCheckpointSize { get; set; }

    /// <summary>
    /// 内存预算触发的提示信息（soft 模式警告 / hard 模式错误）。与 <see cref="ErrorMessage"/> 区分：
    /// 本字段专门承载 P6 运行时事件，UI 可单独高亮。
    /// </summary>
    public string? MemoryWarning { get; set; }

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
        DeleteQuietly(RulesFilePath);
        DeleteQuietly(BaselineSnapshotPath);
        // P6.2 增量分析：检查点/断点/基线均为用户上传的临时文件
        DeleteQuietly(CheckpointPath);
        DeleteQuietly(ResumeFrom);
        DeleteQuietly(BaselinePath);
    }

    private static void DeleteQuietly(string? path)
    {
        if (path == null) return;
        try { File.Delete(path); } catch { }
    }
}
