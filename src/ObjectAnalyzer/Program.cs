using System;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.IO;
using ObjectAnalyzer;
using ObjectAnalyzer.Analysis;
using Spectre.Console;

Console.OutputEncoding = System.Text.Encoding.UTF8;

// 支持 Ctrl+C 优雅取消长任务
var cts = new CancellationTokenSource();

var inputOption = new Option<FileInfo?>(
    aliases: CliAliases.Input,
    description: "指定输入的 JSON、JSONL、CSV、Excel、XML、YAML 文件路径；支持 .gz / .bz2 / .zip 压缩包直读（含 zip 内嵌套 gzip/bzip2 条目，自动识别内层格式）。");

var outputOption = new Option<FileInfo?>(
    aliases: CliAliases.Output,
    description: "指定保存分析报告的目标文件路径 (.json / .html / .md / .csv)。");

var noFlattenOption = new Option<bool>(
    name: "--no-flatten",
    description: "解析 JSON 时，不扁平化嵌套的属性路径。");

var maxValuesOption = new Option<int>(
    aliases: CliAliases.MaxValues,
    description: "控制台报告中每个字段最多展示的唯一值数量。",
    getDefaultValue: () => 5);

var generateTestExcelOption = new Option<bool>(
    name: "--generate-test-excel",
    description: "生成 Excel 测试文件 (samples/test_data.xlsx、samples/test_data_excel_json.xlsx、samples/test_data_excel_json_multi_columns.xlsx) 并退出。");

var typeOption = new Option<string?>(
    aliases: CliAliases.Type,
    description: "指定 .cs 类型定义文件路径，用于强类型反序列化；可带类名: \"UserInfo.cs:User\"（.cs 含多个类时冒号后指定）。");

var rulesOption = new Option<FileInfo?>(
    aliases: CliAliases.Rules,
    description: "指定字段规则配置文件路径 (.json)，用于定义默认值、主导类型、枚举约束以及字段值转换（解密 / JWT 转明文等）。");

var fieldsOption = new Option<string?>(
    aliases: CliAliases.Fields,
    description: "仅统计这些字段（逗号分隔）。与规则文件的 transform 配合时，字段值将按规则转换后再统计。");

var whereOption = new Option<string?>(
    aliases: CliAliases.Where,
    description: "按字段值过滤对象后再统计（如 \"Age > 18 AND Status in ['A','B']\"）。支持 == != > < >= <=、in/not in、like/not like、AND/OR 与括号；过滤在字段选择与值转换之后执行。");

var parallelOption = new Option<int>(
    aliases: CliAliases.Parallel,
    description: "指定分析时的最大并行线程数 (默认使用所有核心)。",
    getDefaultValue: () => -1);

var violationsOption = new Option<FileInfo?>(
    aliases: CliAliases.Violations,
    description: "将质量检查违规（枚举/范围/模式/必填/唯一/null 率 + 数据集期望）导出为独立的结构化 JSON 文件路径。");

var qualityOption = new Option<FileInfo?>(
    aliases: CliAliases.Quality,
    description: "导出结构化数据质量报告 JSON 文件路径（Pass/Fail 摘要 + 违规率 + 逐字段明细）。");

var sampleReservoirOption = new Option<int>(
    name: "--sample-reservoir",
    description: "蓄水池随机抽样：从海量数据中均匀随机抽取最多 N 个对象分析，快速预估 Schema（如 --sample-reservoir 50000）。与 --sample-step 互斥。",
    getDefaultValue: () => 0);

var sampleStepOption = new Option<int>(
    name: "--sample-step",
    description: "等距系统抽样：每 N 条记录取 1 条分析，快速预估 Schema（如 --sample-step 100）。与 --sample-reservoir 互斥。",
    getDefaultValue: () => 0);

var failOnViolationsOption = new Option<bool>(
    name: "--fail-on-violations",
    description: "存在枚举违规时以非零退出码结束（便于 CI 数据质量门禁）。");

var exportSchemaOption = new Option<string?>(
    aliases: CliAliases.ExportSchema,
    description: "将推断出的数据结构自动导出为代码定义: csharp, typescript, json-schema, openapi, openapi-yaml, proto, sql (postgres/mysql/sqlite/sqlserver)；可带输出路径: \"csharp:out.cs\"（缺省自动推导）。");
exportSchemaOption.AddCompletions("csharp", "typescript", "json-schema", "openapi", "openapi-yaml", "proto", "sql", "mysql", "sqlite", "sqlserver");

var connectionOption = new Option<string?>(
    name: "--connection",
    description: "数据库直连：输入连接字符串（如 \"Host=localhost;Database=mydb\"），配合 --query 流式读取查询结果进行分析，无需先导出为文件。");

var queryOption = new Option<string?>(
    name: "--query",
    description: "数据库直连时执行的 SQL 查询（如 \"SELECT * FROM users\"）。");

var providerOption = new Option<string?>(
    name: "--provider",
    description: "数据库类型: postgres, mysql, sqlite, sqlserver；缺省根据连接字符串关键字自动推断（建议显式指定）。");
providerOption.AddCompletions("postgres", "mysql", "sqlite", "sqlserver");

var piiReportOption = new Option<FileInfo?>(
    name: "--pii-report",
    description: "导出数据安全合规等级报告 JSON 文件路径（合规等级 A/B/C + 敏感字段清单 + 打码样本）。");

var piiOffOption = new Option<bool>(
    name: "--pii-off",
    description: "全局关闭 PII 识别与打码：报告输出原始值（已知数据不含敏感信息或需完整原文时使用）。");

var failOnPiiLevelOption = new Option<string?>(
    name: "--fail-on-pii-level",
    description: "CI 合规门禁：合规等级达到或劣于指定级别时以非零退出码结束（C=仅 C 级失败；B=发现任何敏感字段即失败）。与 --fail-on-violations 对称。");
failOnPiiLevelOption.AddCompletions("A", "B", "C");

var failOnOutlierRatioOption = new Option<double?>(
    name: "--fail-on-outlier-ratio",
    description: "CI 异常值门禁：任一数值字段的 IQR 异常值比例（0.0~1.0）超过该阈值时以退出码 5 结束。与 --fail-on-violations 对称。规则文件 fields.<name>.maxOutlierRatio 可按字段设置。");

var schemaSnapshotOption = new Option<string?>(
    name: "--export-schema-snapshot",
    description: "导出 canonical Schema 快照到指定 JSON 路径（字段名/类型/coverage/nullable/语义/范围/枚举/异常值）。独立于 --export-schema 的代码生成器；与 --diff-schema 配合做 schema 演进对比。");

var diffSchemaOption = new Option<string?>(
    name: "--diff-schema",
    description: "与历史 Schema 快照做 diff（指定基线 JSON 文件路径）。输出新增/删除/变更字段，破坏性变更可触发退出码 6。配合 --export-schema-snapshot 形成 CI 演进门禁。");

var schemaDiffReportOption = new Option<string?>(
    name: "--schema-diff-report",
    description: "Schema diff 结果导出到指定 JSON 文件（结构化输出，便于下游消费）。未指定时仅在控制台与主报告展示。");

var allowBreakingSchemaChangesOption = new Option<bool>(
    name: "--allow-breaking-schema-changes",
    description: "Schema 演进门禁：true（默认）= 允许破坏性变更；false = 含破坏性变更时以退出码 6 结束（与 --fail-on-violations 对称）。",
    getDefaultValue: () => true);

var rootCommand = new RootCommand("对象字段分析统计工具 (Object Analyzer)");
rootCommand.AddOption(inputOption);
rootCommand.AddOption(outputOption);
rootCommand.AddOption(noFlattenOption);
rootCommand.AddOption(maxValuesOption);
rootCommand.AddOption(generateTestExcelOption);
rootCommand.AddOption(typeOption);
rootCommand.AddOption(rulesOption);
rootCommand.AddOption(fieldsOption);
rootCommand.AddOption(whereOption);
rootCommand.AddOption(parallelOption);
rootCommand.AddOption(violationsOption);
rootCommand.AddOption(qualityOption);
rootCommand.AddOption(failOnViolationsOption);
rootCommand.AddOption(sampleReservoirOption);
rootCommand.AddOption(sampleStepOption);
rootCommand.AddOption(exportSchemaOption);
rootCommand.AddOption(connectionOption);
rootCommand.AddOption(queryOption);
rootCommand.AddOption(providerOption);
rootCommand.AddOption(piiReportOption);
rootCommand.AddOption(piiOffOption);
rootCommand.AddOption(failOnPiiLevelOption);
rootCommand.AddOption(failOnOutlierRatioOption);
rootCommand.AddOption(schemaSnapshotOption);
rootCommand.AddOption(diffSchemaOption);
rootCommand.AddOption(schemaDiffReportOption);
rootCommand.AddOption(allowBreakingSchemaChangesOption);

rootCommand.SetHandler((InvocationContext context) =>
{
    var parseResult = context.ParseResult;
    var input = parseResult.GetValueForOption(inputOption);
    var output = parseResult.GetValueForOption(outputOption);
    var noFlatten = parseResult.GetValueForOption(noFlattenOption);
    var maxValues = parseResult.GetValueForOption(maxValuesOption);
    var generateTestExcel = parseResult.GetValueForOption(generateTestExcelOption);
    var type = parseResult.GetValueForOption(typeOption);
    var rules = parseResult.GetValueForOption(rulesOption);
    var fields = parseResult.GetValueForOption(fieldsOption);
    var where = parseResult.GetValueForOption(whereOption);
    var maxParallel = parseResult.GetValueForOption(parallelOption);
    var violations = parseResult.GetValueForOption(violationsOption);
    var quality = parseResult.GetValueForOption(qualityOption);
    var failOnViolations = parseResult.GetValueForOption(failOnViolationsOption);
    var sampleReservoir = parseResult.GetValueForOption(sampleReservoirOption);
    var sampleStep = parseResult.GetValueForOption(sampleStepOption);
    var exportSchema = parseResult.GetValueForOption(exportSchemaOption);
    var connection = parseResult.GetValueForOption(connectionOption);
    var query = parseResult.GetValueForOption(queryOption);
    var provider = parseResult.GetValueForOption(providerOption);
    var piiReport = parseResult.GetValueForOption(piiReportOption);
    var piiOff = parseResult.GetValueForOption(piiOffOption);
    var failOnPiiLevel = parseResult.GetValueForOption(failOnPiiLevelOption);
    var failOnOutlierRatio = parseResult.GetValueForOption(failOnOutlierRatioOption);
    var schemaSnapshotPath = parseResult.GetValueForOption(schemaSnapshotOption);
    var diffSchemaBaseline = parseResult.GetValueForOption(diffSchemaOption);
    var schemaDiffReportPath = parseResult.GetValueForOption(schemaDiffReportOption);
    var allowBreakingSchemaChanges = parseResult.GetValueForOption(allowBreakingSchemaChangesOption);

    if (generateTestExcel)
    {
        TestFileGenerator.GenerateAll();
        return;
    }

    if (maxParallel < 1 && maxParallel != -1)
    {
        AnsiConsole.MarkupLine("[red]错误: --parallel 必须是正整数 (>= 1) 或 -1 (默认)。[/]");
        context.ExitCode = 1;
        return;
    }

    try
    {
        Sampling.Validate(sampleReservoir, sampleStep);
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"[red]错误: {Markup.Escape(ex.Message)}[/]");
        context.ExitCode = 1;
        return;
    }

    if (!string.IsNullOrEmpty(query) && string.IsNullOrEmpty(connection))
    {
        AnsiConsole.MarkupLine("[red]错误: 使用 --query 时必须同时提供 --connection。[/]");
        context.ExitCode = 1;
        return;
    }

    if (!string.IsNullOrWhiteSpace(failOnPiiLevel))
    {
        string normalized = failOnPiiLevel.Trim().ToUpperInvariant();
        if (normalized is not ("A" or "B" or "C"))
        {
            AnsiConsole.MarkupLine("[red]错误: --fail-on-pii-level 只接受 A / B / C。[/]");
            context.ExitCode = 1;
            return;
        }
        failOnPiiLevel = normalized;
    }

    if (piiOff && !string.IsNullOrWhiteSpace(failOnPiiLevel))
    {
        AnsiConsole.MarkupLine("[red]错误: --pii-off 与 --fail-on-pii-level 互斥（关闭 PII 识别后无法评估合规等级）。[/]");
        context.ExitCode = 1;
        return;
    }

    if (piiOff && piiReport != null)
    {
        AnsiConsole.MarkupLine("[yellow]警告: 已指定 --pii-off，忽略 --pii-report。[/]");
        piiReport = null;
    }

    if (input == null && string.IsNullOrEmpty(connection))
    {
        AnsiConsole.MarkupLine("[red]错误: 未指定输入文件或数据库连接。使用 -i 指定文件，或使用 --connection + --query 直连数据库。[/]");
        AnsiConsole.MarkupLine("[grey]提示: 使用 -h 或 --help 查看使用说明。[/]");
        context.ExitCode = 1;
        return;
    }

    var (typeFile, typeName) = SplitColonSuffix(type);
    var (schemaFormat, schemaOutput) = SplitColonSuffix(exportSchema);

    var options = new AnalysisOptions
    {
        InputPath = input?.FullName ?? "",
        OutputPath = output?.FullName,
        Flatten = !noFlatten,
        MaxValuesToShow = maxValues,
        TypeFile = typeFile,
        TypeName = typeName,
        RulesFile = rules?.FullName,
        MaxDegreeOfParallelism = maxParallel,
        Fields = fields,
        Filter = where,
        ViolationsPath = violations?.FullName,
        QualityPath = quality?.FullName,
        FailOnViolations = failOnViolations,
        SampleReservoir = sampleReservoir,
        SampleStep = sampleStep,
        ExportSchema = schemaFormat,
        SchemaOutputPath = schemaOutput,
        ConnectionString = connection,
        Query = query,
        DbProvider = provider,
        PiiPath = piiReport?.FullName,
        PiiOff = piiOff,
        FailOnPiiLevel = failOnPiiLevel,
        FailOnOutlierRatio = failOnOutlierRatio,
        SchemaSnapshotPath = schemaSnapshotPath,
        DiffSchemaBaseline = diffSchemaBaseline,
        SchemaDiffReportPath = schemaDiffReportPath,
        AllowBreakingSchemaChanges = allowBreakingSchemaChanges
    };

    context.ExitCode = AnalysisRunner.Run(options, cts.Token);
});

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

return rootCommand.Invoke(args);

/// <summary>
/// 切分 "前缀[:后缀]"（如 "File.cs:ClassName"、"csharp:out.cs"）。
/// 跳过 Windows 盘符冒号（如 "C:\x" 中的 'C:'），因此 "csharp:C:\out.cs" 会正确切分为 ("csharp","C:\out.cs")。
/// 无可用冒号时原样作为路径。
/// </summary>
static (string? path, string? suffix) SplitColonSuffix(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
        return (null, null);
    bool driveLetter = value.Length >= 2 && value[1] == ':' && char.IsAsciiLetter(value[0]);
    for (int i = 0; i < value.Length; i++)
    {
        if (value[i] != ':')
            continue;
        if (driveLetter && i == 1)
            continue; // 盘符冒号不算分隔符
        if (i > 0 && i < value.Length - 1)
            return (value[..i], value[(i + 1)..]);
        break;
    }
    return (value, null);
}

static class CliAliases
{
    public static readonly string[] Input = { "--input", "-i" };
    public static readonly string[] Output = { "--output", "-o" };
    public static readonly string[] MaxValues = { "--max-values", "-m" };
    public static readonly string[] Type = { "--type", "-t" };
    public static readonly string[] Rules = { "--rules", "-r" };
    public static readonly string[] Fields = { "--fields", "-F" };
    public static readonly string[] Where = { "--where", "-w" };
    public static readonly string[] Parallel = { "--parallel", "-p" };
    public static readonly string[] Violations = { "--violations", "-v" };
    public static readonly string[] Quality = { "--quality", "-q" };
    public static readonly string[] ExportSchema = { "--export-schema", "-s" };
}
