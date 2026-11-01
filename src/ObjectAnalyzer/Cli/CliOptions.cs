using System.CommandLine;

namespace ObjectAnalyzer.Cli;

internal sealed class CliOptions
{
    public CliOptions()
    {
        ExportSchema.CompletionSources.Add(["csharp", "typescript", "json-schema", "openapi", "openapi-yaml", "proto", "sql", "mysql", "sqlite", "sqlserver"]);
        Provider.CompletionSources.Add(["postgres", "mysql", "sqlite", "sqlserver"]);
        FailOnPiiLevel.CompletionSources.Add(["A", "B", "C"]);
    }

    public Option<FileInfo?> Input { get; } = Create<FileInfo?>(
        "--input",
        "指定输入的 JSON、JSONL、CSV、Excel、XML、YAML 文件路径；支持 .gz / .bz2 / .zip 压缩包直读（含 zip 内嵌套 gzip/bzip2 条目，自动识别内层格式）。",
        CliAliases.Input);

    public Option<FileInfo?> Output { get; } = Create<FileInfo?>(
        "--output",
        "指定保存分析报告的目标文件路径 (.json / .html / .md / .csv)。",
        CliAliases.Output);

    public Option<bool> NoFlatten { get; } = Create<bool>(
        "--no-flatten",
        "解析 JSON 时，不扁平化嵌套的属性路径。");

    public Option<int> MaxValues { get; } = Create(
        "--max-values",
        "控制台报告中每个字段最多展示的唯一值数量（仅影响展示；导出报告与 API/Web 结果不受影响，分析上限见 --max-tracked-values）。",
        5,
        CliAliases.MaxValues);

    public Option<int> MaxTrackedValues { get; } = Create(
        "--max-tracked-values",
        "分析引擎每字段实际跟踪的唯一值上限，超出部分独立记录为 OtherValueCount（影响内存占用与 valueCounts 精度）。",
        1000,
        CliAliases.MaxTrackedValues);

    public Option<bool> GenerateTestExcel { get; } = Create<bool>(
        "--generate-test-excel",
        "生成 Excel 测试文件 (samples/test_data.xlsx、samples/test_data_excel_json.xlsx、samples/test_data_excel_json_multi_columns.xlsx) 并退出。");

    public Option<FileInfo?> Rules { get; } = Create<FileInfo?>(
        "--rules",
        "指定字段规则配置文件路径 (.json)，用于定义默认值、主导类型、枚举约束以及字段值转换（解密 / JWT 转明文等）。",
        CliAliases.Rules);

    public Option<bool> AllowUnknownRules { get; } = Create<bool>(
        "--allow-unknown-rules",
        "规则文件里存在不认识的键（拼错或已废弃）时仍继续分析。默认会报错中止，因为这些键会被静默忽略——规则看似生效实则没有。");

    public Option<bool> NoCsvNumberInference { get; } = Create<bool>(
        "--no-csv-number-inference",
        "关闭 CSV 数值推断：数字列一律按字符串统计。默认开启——CSV 无类型信息，不推断的话数值列拿不到均值/标准差/分位数，相关性分析也无从计算。");

    public Option<string?> Fields { get; } = Create<string?>(
        "--fields",
        "仅统计这些字段（逗号分隔）。与规则文件的 transform 配合时，字段值将按规则转换后再统计。",
        CliAliases.Fields);

    public Option<string?> Where { get; } = Create<string?>(
        "--where",
        "按字段值过滤对象后再统计（如 \"Age > 18 AND Status in ['A','B']\"）。支持 == != > < >= <=、in/not in、like/not like、AND/OR 与括号；过滤在字段选择与值转换之后执行。",
        CliAliases.Where);

    public Option<int> Parallel { get; } = Create(
        "--parallel",
        "指定分析时的最大并行线程数 (默认使用所有核心)。",
        -1,
        CliAliases.Parallel);

    public Option<FileInfo?> Violations { get; } = Create<FileInfo?>(
        "--violations",
        "将质量检查违规（枚举/范围/模式/必填/唯一/null 率 + 数据集期望）导出为独立的结构化 JSON 文件路径。",
        CliAliases.Violations);

    public Option<FileInfo?> Quality { get; } = Create<FileInfo?>(
        "--quality",
        "导出结构化数据质量报告 JSON 文件路径（Pass/Fail 摘要 + 违规率 + 逐字段明细）。",
        CliAliases.Quality);

    public Option<int> SampleReservoir { get; } = Create(
        "--sample-reservoir",
        "蓄水池随机抽样：从海量数据中均匀随机抽取最多 N 个对象分析，快速预估 Schema（如 --sample-reservoir 50000）。与 --sample-step 互斥。",
        0);

    public Option<int> SampleStep { get; } = Create(
        "--sample-step",
        "等距系统抽样：每 N 条记录取 1 条分析，快速预估 Schema（如 --sample-step 100）。与 --sample-reservoir 互斥。",
        0);

    public Option<bool> FailOnViolations { get; } = Create<bool>(
        "--fail-on-violations",
        "存在枚举违规时以非零退出码结束（便于 CI 数据质量门禁）。");

    public Option<string?> ExportSchema { get; } = Create<string?>(
        "--export-schema",
        "将推断出的数据结构自动导出为代码定义: csharp, typescript, json-schema, openapi, openapi-yaml, proto, sql (postgres/mysql/sqlite/sqlserver)；可带输出路径: \"csharp:out.cs\"（缺省自动推导）。",
        CliAliases.ExportSchema);

    public Option<string?> Connection { get; } = Create<string?>(
        "--connection",
        "数据库直连：输入连接字符串（如 \"Host=localhost;Database=mydb\"），配合 --query 流式读取查询结果进行分析，无需先导出为文件。");

    public Option<string?> Query { get; } = Create<string?>(
        "--query",
        "数据库直连时执行的 SQL 查询（如 \"SELECT * FROM users\"）。");

    public Option<string?> Provider { get; } = Create<string?>(
        "--provider",
        "数据库类型: postgres, mysql, sqlite, sqlserver；缺省根据连接字符串关键字自动推断（建议显式指定）。");

    public Option<FileInfo?> PiiReport { get; } = Create<FileInfo?>(
        "--pii-report",
        "导出数据安全合规等级报告 JSON 文件路径（合规等级 A/B/C + 敏感字段清单 + 打码样本）。");

    public Option<bool> PiiOff { get; } = Create<bool>(
        "--pii-off",
        "全局关闭 PII 识别与打码：报告输出原始值（已知数据不含敏感信息或需完整原文时使用）。");

    public Option<string?> FailOnPiiLevel { get; } = Create<string?>(
        "--fail-on-pii-level",
        "CI 合规门禁：合规等级达到或劣于指定级别时以非零退出码结束（C=仅 C 级失败；B=发现任何敏感字段即失败）。与 --fail-on-violations 对称。");

    public Option<double?> FailOnOutlierRatio { get; } = Create<double?>(
        "--fail-on-outlier-ratio",
        "CI 异常值门禁：任一数值字段的 IQR 异常值比例（0.0~1.0）超过该阈值时以退出码 5 结束。与 --fail-on-violations 对称。规则文件 fields.<name>.maxOutlierRatio 可按字段设置。");

    public Option<string?> SchemaSnapshot { get; } = Create<string?>(
        "--export-schema-snapshot",
        "导出 canonical Schema 快照到指定 JSON 路径（字段名/类型/coverage/nullable/语义/范围/枚举/异常值）。独立于 --export-schema 的代码生成器；与 --diff-schema 配合做 schema 演进对比。");

    public Option<string?> DiffSchema { get; } = Create<string?>(
        "--diff-schema",
        "与历史 Schema 快照做 diff（指定基线 JSON 文件路径）。输出新增/删除/变更字段，破坏性变更可触发退出码 6。配合 --export-schema-snapshot 形成 CI 演进门禁。");

    public Option<string?> SchemaDiffReport { get; } = Create<string?>(
        "--schema-diff-report",
        "Schema diff 结果导出到指定 JSON 文件（结构化输出，便于下游消费）。未指定时仅在控制台与主报告展示。");

    public Option<bool> AllowBreakingSchemaChanges { get; } = Create(
        "--allow-breaking-schema-changes",
        "Schema 演进门禁：true（默认）= 允许破坏性变更；false = 含破坏性变更时以退出码 6 结束（与 --fail-on-violations 对称）。",
        true);

    public Option<string?> RootPath { get; } = Create<string?>(
        "--root-path",
        "在文件根应用 JSONPath 表达式（RFC 9535），从整体文件中提取子节点作为分析输入（如 --root-path '$.data[*]'，从 {data:[...]} 中只分析 data 数组）。仅对整体 JSON 文件生效；语法无效时报错，匹配空集时警告后继续。");

    public Option<string?> RecordPath { get; } = Create<string?>(
        "--record-path",
        "在每条记录上应用 JSONPath 表达式（RFC 9535），提取子节点替换为新记录后再分析（如 JSONL 每行 {event,payload}，--record-path '$.payload' 只分析 payload）。对 JSON / JSONL / CSV / Excel / DB 均生效；语法无效时报错，匹配空集时警告后继续。");

    public Option<string?> AuditLog { get; } = Create<string?>(
        "--audit-log",
        "审计日志输出路径（NDJSON 格式，每行一条 JSON）。记录 analysis_start / analysis_finish / analysis_cancel / analysis_error 四类事件，仅包含输入 SHA-256 哈希 / 操作者 / 关键选项 / 结果摘要，不写入字段值。适合合规留痕。");

    public Option<string?> Operator { get; } = Create<string?>(
        "--operator",
        "操作者标识，写入审计日志的每条事件（--audit-log 启用时生效）。推荐填入执行者 ID（如 \"[email protected]\" / \"ci-runner-12\"），便于溯源。",
        "anonymous");

    public Option<bool> NoProgress { get; } = Create<bool>(
        "--no-progress",
        "关闭分析过程中的 Spectre.Console 进度条 + ETA，回归到 spinner 行为。典型场景：CI 日志捕获、脚本管道、已重定向到文件时希望避免 ANSI 控制码污染输出。默认开启进度条。");

    public Option<string?> Checkpoint { get; } = Create<string?>(
        "--checkpoint",
        "P6.2：检查点输出路径。本次分析成功完成后保存 v3 计算状态和 JSONL 已处理行位置，下次可用 --resume <PATH> 续跑。原子写入（先 .tmp 再 rename）避免半写状态。");

    public Option<string?> Resume { get; } = Create<string?>(
        "--resume",
        "P6.2：从检查点续跑。指定 --resume <PATH> 加载之前 --checkpoint 写入的快照，解析器从已处理位置之后继续读取新数据，结果与快照合并后输出。源文件路径不一致时拒绝续跑。");

    public Option<string?> Baseline { get; } = Create<string?>(
        "--baseline",
        "历史 v3 计算状态快照路径。将本批新增数据与历史状态合并；普通 AnalysisResult 报告不能用于续算。--resume 跳过同一 JSONL 源已处理的行，--baseline 处理独立批次。");

    public Option<long> MaxMemory { get; } = Create(
        "--max-memory",
        "P6.3：进程托管堆上限（MB）。仅监控 .NET 托管堆。默认 0（不监控）。典型场景：50GB JSONL 全量物化会 OOM，设 1024 让 --max-memory-mode soft 自动启用采样、或者 hard 立即失败。",
        0L);

    public Option<string> MaxMemoryMode { get; } = Create(
        "--max-memory-mode",
        "P6.3：超限行为（需配合 --max-memory）。soft=警告并自动启用 50K 蓄水池采样；hard=立即以退出码 1 结束。默认 soft。",
        "soft");

    public Option<bool> PreflightOnly { get; } = Create<bool>(
        "--preflight-only",
        "仅做预检：解析数据样本与规则字段对比，输出匹配情况后退出，不进行正式分析。CI 中可在跑全量前先确认规则与数据是否匹配。");

    public Option<bool> Yes { get; } = Create<bool>(
        "--yes",
        "跳过交互式确认（包括预检警告、覆盖已存在文件等），适合 CI / 脚本场景。",
        CliAliases.Yes);

    public void AddTo(RootCommand command)
    {
        Option[] options =
        [
            Input, Output, NoFlatten, MaxValues, MaxTrackedValues, GenerateTestExcel, Rules,
            AllowUnknownRules, NoCsvNumberInference, Fields, Where, Parallel, Violations, Quality,
            FailOnViolations, SampleReservoir, SampleStep, ExportSchema, Connection, Query, Provider,
            PiiReport, PiiOff, FailOnPiiLevel, FailOnOutlierRatio, SchemaSnapshot, DiffSchema,
            SchemaDiffReport, AllowBreakingSchemaChanges, RootPath, RecordPath, AuditLog, Operator,
            NoProgress, Checkpoint, Resume, Baseline, MaxMemory, MaxMemoryMode, PreflightOnly, Yes
        ];
        foreach (Option option in options)
            command.Options.Add(option);
    }

    public CliArguments Read(ParseResult parseResult) => new(
        Input: parseResult.GetValue(Input),
        Output: parseResult.GetValue(Output),
        NoFlatten: parseResult.GetValue(NoFlatten),
        MaxValues: parseResult.GetValue(MaxValues),
        MaxTrackedValues: parseResult.GetValue(MaxTrackedValues),
        GenerateTestExcel: parseResult.GetValue(GenerateTestExcel),
        Rules: parseResult.GetValue(Rules),
        AllowUnknownRules: parseResult.GetValue(AllowUnknownRules),
        NoCsvNumberInference: parseResult.GetValue(NoCsvNumberInference),
        Fields: parseResult.GetValue(Fields),
        Where: parseResult.GetValue(Where),
        MaxParallel: parseResult.GetValue(Parallel),
        Violations: parseResult.GetValue(Violations),
        Quality: parseResult.GetValue(Quality),
        FailOnViolations: parseResult.GetValue(FailOnViolations),
        SampleReservoir: parseResult.GetValue(SampleReservoir),
        SampleStep: parseResult.GetValue(SampleStep),
        ExportSchema: parseResult.GetValue(ExportSchema),
        Connection: parseResult.GetValue(Connection),
        Query: parseResult.GetValue(Query),
        Provider: parseResult.GetValue(Provider),
        PiiReport: parseResult.GetValue(PiiReport),
        PiiOff: parseResult.GetValue(PiiOff),
        FailOnPiiLevel: parseResult.GetValue(FailOnPiiLevel),
        FailOnOutlierRatio: parseResult.GetValue(FailOnOutlierRatio),
        SchemaSnapshot: parseResult.GetValue(SchemaSnapshot),
        DiffSchema: parseResult.GetValue(DiffSchema),
        SchemaDiffReport: parseResult.GetValue(SchemaDiffReport),
        AllowBreakingSchemaChanges: parseResult.GetValue(AllowBreakingSchemaChanges),
        RootPath: parseResult.GetValue(RootPath),
        RecordPath: parseResult.GetValue(RecordPath),
        AuditLog: parseResult.GetValue(AuditLog),
        Operator: parseResult.GetValue(Operator) ?? "anonymous",
        NoProgress: parseResult.GetValue(NoProgress),
        Checkpoint: parseResult.GetValue(Checkpoint),
        Resume: parseResult.GetValue(Resume),
        Baseline: parseResult.GetValue(Baseline),
        MaxMemoryMB: parseResult.GetValue(MaxMemory),
        MaxMemoryMode: parseResult.GetValue(MaxMemoryMode) ?? "soft",
        PreflightOnly: parseResult.GetValue(PreflightOnly),
        Yes: parseResult.GetValue(Yes));

    private static Option<T> Create<T>(string name, string description, string[] aliases) =>
        new(name, aliases) { Description = description };

    private static Option<T> Create<T>(string name, string description) =>
        new(name, []) { Description = description };

    private static Option<T> Create<T>(string name, string description, T defaultValue, string[]? aliases = null)
    {
        var option = new Option<T>(name, aliases ?? []) { Description = description };
        option.DefaultValueFactory = _ => defaultValue;
        return option;
    }
}
