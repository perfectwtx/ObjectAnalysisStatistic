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
}
