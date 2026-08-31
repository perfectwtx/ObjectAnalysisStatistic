using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ObjectAnalyzer.Analysis;

/// <summary>
/// 分析运行配置（"怎么跑"）。与 <see cref="AnalysisOptions"/> 1:1 映射但按概念分组。
/// 加载顺序：CLI / API 显式值 &gt; 本类字段 &gt; <see cref="AnalysisOptions"/> 内置默认。
/// 所有字段均可为 null —— null 表示"未指定，用默认"；非 null 表示"用这个"。
/// <para>
/// 不进本类的字段（仍仅通过 CLI / API 调用方传入）：
/// <list type="bullet">
///   <item>输入源：<c>InputPath</c>、DB 直连（<c>ConnectionString</c> / <c>Query</c> / <c>DbProvider</c>）</item>
///   <item>规则文件路径：<c>RulesFile</c></item>
///   <item>库内部：<c>MemoryFallbackSampleSize</c></item>
/// </list>
/// </para>
/// </summary>
public class AnalysisRuntime
{
    // ── 解析行为 ──

    /// <summary>嵌套属性是否展平为 <c>a.b.c</c>。与 <c>--no-flatten</c> 对应。</summary>
    [JsonPropertyName("flatten")]
    public bool? Flatten { get; set; }

    /// <summary>CSV 数字列是否保守推断为 long / double。与 <c>--no-csv-number-inference</c> 对应。</summary>
    [JsonPropertyName("csvInferNumbers")]
    public bool? CsvInferNumbers { get; set; }

    /// <summary>每字段最多展示的 distinct 数（控制台 / 报告）。与 <c>--max-values</c> 对应。</summary>
    [JsonPropertyName("maxValuesToShow")]
    public int? MaxValuesToShow { get; set; }

    /// <summary>最大并行线程数，-1 表示使用所有核。与 <c>--parallel</c> 对应。</summary>
    [JsonPropertyName("maxParallelism")]
    public int? MaxParallelism { get; set; }

    /// <summary>是否放行拼写错 / 废弃的规则键。默认 false = 严格（错误中止并给建议键名）。与 <c>--allow-unknown-rules</c> 对应。</summary>
    [JsonPropertyName("allowUnknownRules")]
    public bool? AllowUnknownRules { get; set; }

    // ── 字段选择（统一收敛到 runtime 节点；旧顶层 selectedFields / filter 已移除）──

    /// <summary>仅统计指定字段。null / 空 = 不限。与 <c>--fields</c> 对应。</summary>
    [JsonPropertyName("selectedFields")]
    public List<string>? SelectedFields { get; set; }

    /// <summary>记录级过滤表达式（<c>==/!=/&gt;/&lt;</c> 等 + <c>in/like</c>）。与 <c>--where</c> 对应。</summary>
    [JsonPropertyName("filter")]
    public string? Filter { get; set; }

    // ── 采样 / 投影 / 演进 / 漂移 / 合规 / 门禁 / P4 / P6 / 输出 ──

    /// <summary>采样策略（蓄水池 / 等距系统）。</summary>
    [JsonPropertyName("sampling")]
    public SamplingConfig? Sampling { get; set; }

    /// <summary>JSON 输入投影路径。</summary>
    [JsonPropertyName("jsonPath")]
    public JsonPathConfig? JsonPath { get; set; }

    /// <summary>Schema 演进（快照 / Diff / 门禁）。</summary>
    [JsonPropertyName("schema")]
    public SchemaConfig? Schema { get; set; }

    /// <summary>分布漂移（t-digest + HLL 草图）。</summary>
    [JsonPropertyName("distribution")]
    public DistributionConfig? Distribution { get; set; }

    /// <summary>PII 与合规等级。</summary>
    [JsonPropertyName("pii")]
    public PiiRuntimeConfig? Pii { get; set; }

    /// <summary>CI 质量门禁（违规则 / 异常值比例门禁）。</summary>
    [JsonPropertyName("gates")]
    public GatesConfig? Gates { get; set; }

    /// <summary>P4 深度分析（默认全关）。</summary>
    [JsonPropertyName("p4")]
    public P4Config? P4 { get; set; }

    /// <summary>P6 增量分析（检查点落盘 / 续跑 / 基线合并）。</summary>
    [JsonPropertyName("checkpoint")]
    public CheckpointConfig? Checkpoint { get; set; }

    /// <summary>P6 内存预算监控。</summary>
    [JsonPropertyName("memory")]
    public MemoryConfig? Memory { get; set; }

    /// <summary>进度显示开关。</summary>
    [JsonPropertyName("progress")]
    public ProgressConfig? Progress { get; set; }

    /// <summary>审计日志（NDJSON，绝不记字段值与密钥）。</summary>
    [JsonPropertyName("audit")]
    public AuditConfig? Audit { get; set; }

    /// <summary>多格式报告输出。</summary>
    [JsonPropertyName("outputs")]
    public OutputsConfig? Outputs { get; set; }
}
