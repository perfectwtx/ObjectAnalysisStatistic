namespace ObjectAnalyzer.Analysis;

/// <summary>
/// Encapsulates all options for an analysis run, avoiding long parameter lists.
/// </summary>
public sealed class AnalysisOptions
{
    public string InputPath { get; set; } = "";
    public string? OutputPath { get; set; }
    public bool Flatten { get; set; } = true;
    public int MaxValuesToShow { get; set; } = 5;
    public string? RulesFile { get; set; }

    /// <summary>
    /// 规则文件里出现不认识的键（拼错 / 已废弃）时仍然继续分析（--allow-unknown-rules）。
    /// 默认 false —— 直接报错中止，避免规则看似生效实则被静默忽略。
    /// </summary>
    public bool AllowUnknownRules { get; set; }
    public string? ViolationsPath { get; set; }
    public string? QualityPath { get; set; }
    public bool FailOnViolations { get; set; }
    public int MaxDegreeOfParallelism { get; set; } = -1; // -1 = 使用默认（所有核心）
    public string? ExportSchema { get; set; } // "csharp", "typescript", "json-schema"
    public string? SchemaOutputPath { get; set; }

    /// <summary>仅统计这些字段（逗号分隔，来自 --fields）。</summary>
    public string? Fields { get; set; }

    /// <summary>按字段值过滤对象后再统计（--where，如 "Age &gt; 18 AND Status in ['A','B']"）。</summary>
    public string? Filter { get; set; }

    /// <summary>蓄水池随机抽样容量（--sample-reservoir，>0 启用）。</summary>
    public int SampleReservoir { get; set; }

    /// <summary>等距系统抽样步长（--sample-step，>1 启用）。</summary>
    public int SampleStep { get; set; }

    /// <summary>数据库直连连接字符串（--connection）。非空时进入数据库直连模式。</summary>
    public string? ConnectionString { get; set; }

    /// <summary>数据库直连时执行的 SQL 查询（--query，如 "SELECT * FROM users"）。</summary>
    public string? Query { get; set; }

    /// <summary>数据库类型（--provider：postgres/mysql/sqlite/sqlserver），缺省由连接字符串推断。</summary>
    public string? DbProvider { get; set; }

    /// <summary>导出数据安全合规等级报告 JSON 路径（--pii-report）。</summary>
    public string? PiiPath { get; set; }

    /// <summary>全局关闭 PII 识别与打码（--pii-off），报告输出原始值。</summary>
    public bool PiiOff { get; set; }

    /// <summary>
    /// CI 合规门禁阈值（--fail-on-pii-level：A/B/C）。合规等级达到或劣于该等级时以退出码 4 结束。
    /// C=仅 C 级失败；B=B/C 级失败（发现任何敏感字段即失败）；A=同 B（数据干净时 A 级不触发）。
    /// </summary>
    public string? FailOnPiiLevel { get; set; }

    /// <summary>
    /// 异常值比例上限（0.0~1.0，--fail-on-outlier-ratio）：任一数值字段的 IQR 异常值比例
    /// 超过该阈值时以退出码 5 结束。规则文件的 fields.&lt;name&gt;.maxOutlierRatio 可按字段设置。
    /// </summary>
    public double? FailOnOutlierRatio { get; set; }

    /// <summary>
    /// 导出 canonical Schema 快照到指定 JSON 路径（--export-schema-snapshot）。
    /// 独立于 --export-schema 的代码生成器；作为 schema 演进的基线。
    /// </summary>
    public string? SchemaSnapshotPath { get; set; }

    /// <summary>
    /// 与历史 Schema 快照做 diff 的基线文件路径（--diff-schema）。
    /// 当前分析完成后会与该基线对比，输出新增/删除/变更字段。
    /// </summary>
    public string? DiffSchemaBaseline { get; set; }

    /// <summary>
    /// Schema diff 结果导出路径（--schema-diff-report）。若指定，结果写入该 JSON 文件；
    /// 否则只输出到控制台与主报告。
    /// </summary>
    public string? SchemaDiffReportPath { get; set; }

    /// <summary>
    /// CI Schema 演进门禁（--allow-breaking-schema-changes）：
    /// true（默认）= 破坏性变更不触发非零退出码；false = 含破坏性变更时以退出码 6 结束。
    /// </summary>
    public bool AllowBreakingSchemaChanges { get; set; } = true;

    /// <summary>
    /// 在文件根应用 JSONPath 表达式（RFC 9535），从整体文件中提取子节点作为分析输入（--root-path）。
    /// 典型场景：API 响应被包在 {data: [...], meta: {...}} 里时，只想分析 data 数组。
    /// 仅对 JSON 整体文件生效（其他格式下报错或无效）。
    /// 语法无效时硬错误（fail-fast）；匹配空集时仅警告并继续。
    /// </summary>
    public string? RootPath { get; set; }

    /// <summary>
    /// 在每条记录上应用 JSONPath 表达式（--record-path）。
    /// 典型场景：JSONL 每行形如 {event: '...', payload: {...}}，只想分析 payload 子对象。
    /// 对 JSON / JSONL / CSV / Excel / DB 行均生效（流式友好）。
    /// 语法无效时硬错误；匹配空集时仅警告并继续。
    /// </summary>
    public string? RecordPath { get; set; }

    /// <summary>
    /// 审计日志输出路径（--audit-log）。每行一条 JSON（NDJSON 格式），
    /// 记录 analysis_start / analysis_finish / analysis_error / analysis_cancel 四类事件。
    /// 内容不包含字段值（仅 SHA-256 哈希 + 记录数 + 字段名 + 选项快照），适合合规留痕。
    /// </summary>
    public string? AuditLogPath { get; set; }

    /// <summary>
    /// 操作者标识（--operator），写入审计日志的每条事件。缺省 "anonymous"；
    /// 推荐在 CI/调度器中传入执行者 ID（如 "[email protected]" / "ci-runner-12"）。
    /// </summary>
    public string Operator { get; set; } = "anonymous";

    // ── P4 分析深度增强（CLI 开关）──

    /// <summary>启用字段相关性矩阵分析（Pearson / Spearman，--correlation）。</summary>
    public bool EnableCorrelation { get; set; }

    /// <summary>启用字符串模式自动归纳（--string-patterns），发现 "###-####" 等形态模板及覆盖率。</summary>
    public bool EnableStringPatterns { get; set; }

    /// <summary>启用 Unicode 卫生检查（--unicode-hygiene），检测全/半角混用、不可见字符、控制字符等。</summary>
    public bool EnableUnicodeHygiene { get; set; }

    /// <summary>启用时间序列深度分析（--time-series），对 DateTime 字段做趋势/周期/间隙/脉冲性分析。</summary>
    public bool EnableTimeSeries { get; set; }

    /// <summary>模糊去重目标字段（--fuzzy-dedup-field &lt;FIELD&gt;）。空表示不启用模糊去重。</summary>
    public string? FuzzyDedupField { get; set; }

    /// <summary>分布快照导出路径（--export-distribution-snapshot）。</summary>
    public string? DistributionSnapshotPath { get; set; }

    /// <summary>与历史分布快照做 diff 的基线文件路径（--diff-distribution）。</summary>
    public string? DiffDistributionBaseline { get; set; }

    /// <summary>分布 diff 结果导出路径（--distribution-diff-report）。</summary>
    public string? DistributionDiffReportPath { get; set; }

    /// <summary>采样置信区间宽度阈值（覆盖区间宽度 ≥ 此值视为"低可信"，默认 0.2）。</summary>
    public double LowConfidenceWidth { get; set; } = 0.2;

    // ── P6.1 进度条 / ETA ──

    /// <summary>
    /// 是否在分析过程中显示 Spectre.Console 进度条 + ETA（--no-progress 取反）。默认 true。
    /// 关掉后回退到旧版 spinner 行为；典型场景：CI 日志捕获 / 脚本管道 / 已重定向到文件。
    /// </summary>
    public bool ShowProgress { get; set; } = true;

    // ── P6.2 增量分析 / 断点续跑 ──

    /// <summary>
    /// 检查点输出路径（--checkpoint &lt;PATH&gt;）。指定后，每处理完一个分块（10K 对象）后落盘一次 AnalyzerSnapshot，
    /// 用于崩溃后从最近检查点恢复。原子写入（写临时文件再 rename）防止半写状态。
    /// </summary>
    public string? CheckpointPath { get; set; }

    /// <summary>
    /// 从检查点恢复分析（--resume &lt;PATH&gt;）。AnalyzerSnapshot 记录了源文件位置与已聚合 AnalysisResult，
    /// 解析器会从对应位置继续读取，本 run 完成后 <c>Result.MergeFrom(snapshot.Result)</c> 累计。
    /// </summary>
    public string? ResumeFrom { get; set; }

    /// <summary>
    /// 历史分析结果 JSON 路径（--baseline &lt;PATH&gt;），作为本次分析的基线合并对象。
    /// 本次 run 只分析"新数据"，完成后通过 <c>Result.MergeFrom(baseline)</c> 累加。
    /// 与 --resume 的区别：--resume 从源文件中间续跑；--baseline 用任意已存在的 AnalysisResult JSON 合并。
    /// </summary>
    public string? BaselinePath { get; set; }

    // ── P6.3 内存预算（--max-memory） ──

    /// <summary>
    /// 进程托管堆上限（MB，--max-memory &lt;MB&gt;）。0 表示不监控。
    /// 仅监控 .NET 托管堆（GC.GetTotalMemory），不监控非托管 / 第三方 native 内存。
    /// </summary>
    public long MaxMemoryMB { get; set; }

    /// <summary>
    /// 超限行为（--max-memory-mode soft|hard）：
    /// - soft（默认）：超限时打印警告并自动启用采样（蓄水池 50_000 条），让分析在有限内存下继续。
    /// - hard：超限时立即抛 <see cref="MaxMemoryExceededException"/>，AnalysisRunner 以退出码 1 退出。
    /// </summary>
    public MaxMemoryMode MaxMemoryMode { get; set; } = MaxMemoryMode.Soft;

    /// <summary>
    /// soft 模式下被自动触发的采样容量（仅当用户未指定 SampleReservoir/SampleStep 时生效）。
    /// 默认 50_000 条，足以在大多数 PII / 质量检查场景下保持统计意义，又能把堆控制在 100MB 以内。
    /// </summary>
    public int MemoryFallbackSampleSize { get; set; } = 50_000;
}
