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
    public string? TypeFile { get; set; }
    public string? TypeName { get; set; }
    public string? RulesFile { get; set; }
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
}
