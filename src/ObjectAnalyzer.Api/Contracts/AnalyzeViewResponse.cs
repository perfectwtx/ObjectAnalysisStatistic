using ObjectAnalyzer.Analysis;

namespace ObjectAnalyzer.Api;

/// <summary>
/// 统一分析响应（v2 契约，W8）：按 React 前端五块页面组织——
/// 概览统计（<see cref="Overview"/>）/ 字段明细（<see cref="Fields"/>）/
/// 深度分析（<see cref="DeepAnalysis"/>）/ 数据预览（<see cref="Preview"/>）/ 质量报告（<see cref="Quality"/>）。
/// <para>
/// <see cref="Result"/> 保留完整原始 <see cref="AnalysisResult"/>，前端可渐进迁移；
/// 深度分析未启用时 <see cref="DeepAnalysis"/> 为 null（而非空对象），便于前端区分。
/// </para>
/// </summary>
public sealed record AnalyzeViewResponse
{
    /// <summary>上传的数据文件名（原始文本端点为 "(inline)" 或省略）。</summary>
    public string? FileName { get; init; }

    /// <summary>实际使用的解析格式（含 valueParsers 驱动的 excel-json 切换）。</summary>
    public required string Format { get; init; }

    /// <summary>规则来源：inline / file；本请求未提供规则时省略。</summary>
    public string? RulesSource { get; init; }

    /// <summary>概览统计页：总量 / 唯一性 / 重复 / 字段数 / 违规数等一屏指标。</summary>
    public required AnalyzeOverview Overview { get; init; }

    /// <summary>数据预览页：流经分析管线的样本记录（默认最多 20 条）。</summary>
    public required AnalyzePreviewView Preview { get; init; }

    /// <summary>字段明细页：每字段类型 / 覆盖率 / 值分布 / 数值剖析等。</summary>
    public required IReadOnlyList<FieldStatistic> Fields { get; init; }

    /// <summary>深度分析页：相关性 / 字符串模式 / Unicode 卫生 / 时间序列 / 模糊重复 / 分布漂移 / 采样置信 / Schema 演进。未启用时为 null。</summary>
    public AnalyzeDeepAnalysisView? DeepAnalysis { get; init; }

    /// <summary>质量报告页：字段级违规 + 按检查类型的计数汇总。</summary>
    public required AnalyzeQualityView Quality { get; init; }

    /// <summary>完整原始分析结果（兼容保留；与上方分节视图同源）。</summary>
    public required AnalysisResult Result { get; init; }

    /// <summary>从分析结果 + 预览样本组装五页面视图。</summary>
    public static AnalyzeViewResponse From(
        AnalysisResult result,
        IReadOnlyList<Dictionary<string, object?>>? preview,
        string format,
        string? fileName = null,
        string? rulesSource = null)
    {
        return new AnalyzeViewResponse
        {
            FileName = fileName,
            Format = format,
            RulesSource = rulesSource,
            Overview = AnalyzeOverview.From(result),
            Preview = AnalyzePreviewView.From(preview),
            Fields = result.FieldStatistics,
            DeepAnalysis = AnalyzeDeepAnalysisView.From(result),
            Quality = AnalyzeQualityView.From(result),
            Result = result
        };
    }
}

/// <summary>概览统计页：一屏展示的总量指标。</summary>
public sealed record AnalyzeOverview
{
    /// <summary>分析对象总数。</summary>
    public long TotalObjects { get; init; }

    /// <summary>唯一对象数（HLL 近似）。</summary>
    public long TotalUniqueObjects { get; init; }

    /// <summary>重复对象组总数。</summary>
    public long TotalDuplicateGroups { get; init; }

    /// <summary>发现的字段数。</summary>
    public int FieldCount { get; init; }

    /// <summary>质量违规条数（字段级 + 数据集期望）。</summary>
    public int ViolationCount { get; init; }

    /// <summary>生效的过滤表达式（--where / rules.filter）；未启用时省略。</summary>
    public string? FilterExpression { get; init; }

    public static AnalyzeOverview From(AnalysisResult result) => new()
    {
        TotalObjects = result.TotalObjects,
        TotalUniqueObjects = result.TotalUniqueObjects,
        TotalDuplicateGroups = result.TotalDuplicateGroups,
        FieldCount = result.FieldStatistics.Count,
        ViolationCount = result.QualityViolations.Count,
        FilterExpression = result.FilterExpression
    };
}

/// <summary>数据预览页：列定义 + 行数据（值保持解析后的原始类型）。</summary>
public sealed record AnalyzePreviewView
{
    /// <summary>列名（按行内首次出现顺序；可能超过行数——不同行可有不同字段）。</summary>
    public IReadOnlyList<string> Columns { get; init; } = Array.Empty<string>();

    /// <summary>预览行（键为列名，值为解析后的原始值）。</summary>
    public IReadOnlyList<Dictionary<string, object?>> Rows { get; init; } = Array.Empty<Dictionary<string, object?>>();

    /// <summary>true 表示源记录数超过预览上限，仅截取了前部分。</summary>
    public bool Truncated { get; init; }

    /// <summary>预览默认条数。</summary>
    public const int DefaultLimit = 20;

    public static AnalyzePreviewView From(IReadOnlyList<Dictionary<string, object?>>? preview)
    {
        if (preview is not { Count: > 0 })
            return new AnalyzePreviewView();

        // 列按首次出现顺序去重，前端可按列渲染表格
        var columns = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in preview)
        {
            foreach (var key in row.Keys)
            {
                if (seen.Add(key))
                    columns.Add(key);
            }
        }

        return new AnalyzePreviewView
        {
            Columns = columns,
            Rows = preview,
            Truncated = preview.Count >= DefaultLimit
        };
    }
}

/// <summary>深度分析页：仅收纳深度分析维度的结果（未启用时整体为 null）。</summary>
public sealed record AnalyzeDeepAnalysisView
{
    /// <summary>字段相关性矩阵。</summary>
    public FieldCorrelationMatrix? CorrelationMatrix { get; init; }

    /// <summary>字符串模式画像。</summary>
    public Dictionary<string, StringPatternProfile>? StringPatterns { get; init; }

    /// <summary>Unicode 卫生报告。</summary>
    public Dictionary<string, UnicodeHygieneReport>? UnicodeHygiene { get; init; }

    /// <summary>时间序列分析。</summary>
    public Dictionary<string, TimeSeriesAnalysis>? TimeSeriesAnalyses { get; init; }

    /// <summary>模糊重复聚类报告。</summary>
    public FuzzyDuplicateReport? FuzzyDuplicates { get; init; }

    /// <summary>分布漂移（vs 基线）。</summary>
    public DistributionDiff? DistributionDiff { get; init; }

    /// <summary>采样置信区间（Wilson 95%）。</summary>
    public SamplingConfidence? SamplingConfidence { get; init; }

    /// <summary>Schema 演进 diff（vs 基线）。</summary>
    public SchemaDiff? SchemaDiff { get; init; }

    /// <summary>全部维度为空时返回 null（前端据此隐藏深度分析页签）。</summary>
    public static AnalyzeDeepAnalysisView? From(AnalysisResult result)
    {
        var view = new AnalyzeDeepAnalysisView
        {
            CorrelationMatrix = result.CorrelationMatrix,
            StringPatterns = result.StringPatterns,
            UnicodeHygiene = result.UnicodeHygiene,
            TimeSeriesAnalyses = result.TimeSeriesAnalyses,
            FuzzyDuplicates = result.FuzzyDuplicates,
            DistributionDiff = result.DistributionDiff,
            SamplingConfidence = result.SamplingConfidence,
            SchemaDiff = result.SchemaDiff
        };

        bool hasAny = view.CorrelationMatrix != null || view.StringPatterns != null ||
                      view.UnicodeHygiene != null || view.TimeSeriesAnalyses != null ||
                      view.FuzzyDuplicates != null || view.DistributionDiff != null ||
                      view.SamplingConfidence != null || view.SchemaDiff != null;
        return hasAny ? view : null;
    }
}

/// <summary>质量报告页：字段级违规 + 按检查类型计数。</summary>
public sealed record AnalyzeQualityView
{
    /// <summary>全部质量违规（字段级 + 数据集期望）。</summary>
    public IReadOnlyList<FieldViolation> Violations { get; init; } = Array.Empty<FieldViolation>();

    /// <summary>按检查类型（enum/range/pattern/required/unique/outlier/nullRate/数据集期望）计数。</summary>
    public IReadOnlyDictionary<string, int> CountsByCheck { get; init; } = new Dictionary<string, int>();

    /// <summary>是否存在任何违规。</summary>
    public bool HasViolations { get; init; }

    public static AnalyzeQualityView From(AnalysisResult result)
    {
        var counts = result.QualityViolations
            .GroupBy(v => v.Check, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        return new AnalyzeQualityView
        {
            Violations = result.QualityViolations,
            CountsByCheck = counts,
            HasViolations = result.QualityViolations.Count > 0
        };
    }
}
