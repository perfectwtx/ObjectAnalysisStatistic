using ObjectAnalyzer.Analysis.Options;
using ObjectAnalyzer.Analysis.Rules;

namespace ObjectAnalyzer.Analysis;

/// <summary>
/// 统一分析配置（IR）：按功能和作用拆分为 15 个子组类，组合成一个对象。
/// 加载顺序：CLI / API 显式参数 &gt; 规则文件 runtime 节点 &gt; 子组内置默认。
/// <para>
/// 内部流转的唯一 IR：CLI、API、Web 端均构造此对象；
/// 规则文件 (<see cref="AnalysisRules"/>) 通过 <see cref="RulesToOptionsConverter"/> 一次性转换为该对象，
/// 中间不再有 AnalysisRuntime / *Config 之类的转换层。
/// </para>
/// </summary>
public sealed class AnalysisOptions
{
    // ── 输入源 ──
    public InputSourceOptions Input { get; set; } = new();

    // ── 解析行为 ──
    public ParseBehaviorOptions Parse { get; set; } = new();

    // ── 字段选择 / 过滤 ──
    public FieldSelectionOptions FieldSelection { get; set; } = new();

    // ── 采样 ──
    public SamplingOptions Sampling { get; set; } = new();

    // ── 内存预算 ──
    public MemoryBudgetOptions Memory { get; set; } = new();

    // ── 规则文件 ──
    public RulesOptions Rules { get; set; } = new();

    // ── 进度显示 ──
    public ProgressOptions Progress { get; set; } = new();

    // ── 增量分析 ──
    public IncrementalOptions Incremental { get; set; } = new();

    // ── 质量门禁 ──
    public QualityGateOptions Gates { get; set; } = new();

    // ── PII / 合规 ──
    public PiiOptions Pii { get; set; } = new();

    // ── 报告输出 ──
    public ReportOutputOptions Report { get; set; } = new();

    // ── Schema 演进 ──
    public SchemaEvolutionOptions Schema { get; set; } = new();

    // ── 分布漂移 ──
    public DistributionDriftOptions Distribution { get; set; } = new();

    // ── P4 深度分析 ──
    public AdvancedAnalysisOptions Advanced { get; set; } = new();

    // ── 审计日志 ──
    public AuditOptions Audit { get; set; } = new();

    /// <summary>
    /// 已加载的字段规则（来自规则文件的 <c>fields</c> 节）。
    /// 启动分析时由 AnalysisRunner 自行从 <see cref="RulesOptions.RulesFile"/> 加载后填入。
    /// </summary>
    public Dictionary<string, FieldRule>? FieldRules { get; set; }

    /// <summary>
    /// 已加载的字段值解析器（来自规则文件的 <c>valueParsers</c> 节，如 Excel 单元格内嵌 JSON）。
    /// </summary>
    public List<ValueParserConfig>? ValueParsers { get; set; }

    /// <summary>
    /// 已加载的数据集级期望（来自规则文件的 <c>expectations</c> 节）。
    /// </summary>
    public AnalysisExpectations? Expectations { get; set; }

    /// <summary>
    /// 完整的已加载规则文件（向后兼容字段规则的直接访问，如 PII 规则、transform 等）。
    /// null 表示未提供规则文件。
    /// </summary>
    public AnalysisRules? RulesAll { get; set; }
}
