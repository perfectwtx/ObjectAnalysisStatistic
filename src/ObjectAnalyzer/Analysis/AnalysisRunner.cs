using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using ObjectAnalyzer.Parsing;
using Spectre.Console;

namespace ObjectAnalyzer.Analysis;

public static class AnalysisRunner
{
    private static readonly JsonSerializerOptions s_diffJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // P4 分布快照：缓存最近一次 AnalyzeDetailed 暴露的草图，供 ProcessDistributionSnapshot 使用
    [ThreadStatic]
    private static Dictionary<string, MergeableQuantileSketch>? _lastQuantileSketches;
    [ThreadStatic]
    private static Dictionary<string, HyperLogLog>? _lastDistinctSketches;

    /// <summary>
    /// Runs the analysis pipeline. Returns 0 on success, 1 on failure, 2 on cancellation.
    /// 当 <see cref="AnalysisOptions.AuditLogPath"/> 非空时，写入 NDJSON 审计日志。
    /// </summary>
    public static int Run(AnalysisOptions options, CancellationToken cancellationToken = default)
    {
        AuditLogger? audit = null;
        string runId = AuditLogger.NewRunId();
        var sw = new StopwatchLike();
        try
        {
            if (!string.IsNullOrWhiteSpace(options.AuditLogPath))
            {
                audit = new AuditLogger(options.AuditLogPath);
            }
        }
        catch (Exception ex)
        {
            // 审计日志路径不可写不应阻塞分析主流程；但要给操作者明确提示
            AnsiConsole.MarkupLine(
                $"[yellow]警告: 审计日志初始化失败 ({Markup.Escape(ex.Message)})；本次运行不写入审计记录。[/]");
            audit = null;
        }

        int result = RunCore(options, audit, runId, sw, cancellationToken);

        // 若 RunCore 因异常未写入 finish/error 事件，此处兜底记录
        if (audit != null)
        {
            // RunCore 已负责所有正常路径的写入；此处仅在 RunCore 自身抛出未捕获异常时使用
            audit.Dispose();
        }
        return result;
    }

    private static int RunCore(
        AnalysisOptions options,
        AuditLogger? audit,
        string runId,
        StopwatchLike sw,
        CancellationToken cancellationToken)
    {
        bool dbMode = !string.IsNullOrEmpty(options.ConnectionString);
        if (!dbMode && !File.Exists(options.InputPath))
        {
            AnsiConsole.MarkupLine($"[red]错误: 输入文件不存在: {Markup.Escape(options.InputPath)}[/]");
            return 1;
        }

        AnalysisRules? rules = null;
        if (!string.IsNullOrEmpty(options.RulesFile))
        {
            try
            {
                rules = LoadRules(options.RulesFile);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 规则文件解析/预验证失败（含无效 pattern）快速失败，避免带病分析
                AnsiConsole.MarkupLine($"[red]错误: 规则文件加载失败: {Markup.Escape(ex.Message)}[/]");
                return 1;
            }
        }

        // 采样参数提前校验，避免解析到一半才发现配置非法
        if (Sampling.IsEnabled(options.SampleReservoir, options.SampleStep))
            Sampling.Validate(options.SampleReservoir, options.SampleStep);

        // 数据库直连参数提前校验
        if (dbMode)
        {
            if (string.IsNullOrEmpty(options.Query))
            {
                AnsiConsole.MarkupLine("[red]错误: 使用 --connection 直连数据库时必须指定 --query。[/]");
                return 1;
            }
            try
            {
                DatabaseReader.ResolveProvider(options.DbProvider, options.ConnectionString!);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]错误: {Markup.Escape(ex.Message)}[/]");
                return 1;
            }
        }

        // Schema diff 基线文件预检
        if (!string.IsNullOrEmpty(options.DiffSchemaBaseline) && !File.Exists(options.DiffSchemaBaseline))
        {
            AnsiConsole.MarkupLine(
                $"[red]错误: --diff-schema 基线文件不存在: {Markup.Escape(options.DiffSchemaBaseline)}[/]");
            return 1;
        }

        // JSONPath 表达式语法预检（fail-fast，避免解析到一半才发现）
        if (!string.IsNullOrWhiteSpace(options.RootPath))
        {
            try
            {
                JsonPathProjector.TryValidate(options.RootPath);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]错误: --root-path {Markup.Escape(ex.Message)}[/]");
                return 1;
            }
        }
        if (!string.IsNullOrWhiteSpace(options.RecordPath))
        {
            try
            {
                JsonPathProjector.TryValidate(options.RecordPath);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]错误: --record-path {Markup.Escape(ex.Message)}[/]");
                return 1;
            }
        }

        // 输入存在性与规则加载通过后，审计可正式记录 start 事件（不计入参数校验失败等）
        WriteAuditStart(audit, options, rules, runId);

        try
        {
            IEnumerable<Dictionary<string, object?>> parsedObjects;
            var effectiveValueParsers = ResolveValueParsers(options, rules);

            // JSONPath 投影：先在文件根应用（仅当 --root-path 存在且非数据库/类型模式），
            // 此时跳过原解析器以避免对文件整体读两次；最后在每条记录上应用（--record-path）。
            // 匹配空集时仅警告。
            bool hasRootPath = !string.IsNullOrWhiteSpace(options.RootPath);
            if (hasRootPath)
            {
                // 数据库直连：DbDataReader 惰性流式读取查询结果
                if (dbMode)
                {
                    parsedObjects = ParseFromDatabase(options);
                }
                // 如果指定了 --type，使用强类型反序列化路径
                else if (!string.IsNullOrEmpty(options.TypeFile))
                {
                    parsedObjects = RunTyped(options);
                }
                else
                {
                    string fmt = FileParsers.DetectFormat(options.InputPath);
                    if (fmt is "jsonl" or "ndjson" or "csv" or "excel" or "excel-json" or "db" or "typed")
                    {
                        AnsiConsole.MarkupLine(
                            $"[yellow]警告: --root-path 在 [red]{fmt}[/] 格式下没有文件根概念，将被忽略（仅 --record-path 生效）[/]");
                        parsedObjects = ParseByFormat(options, rules, effectiveValueParsers);
                    }
                    else
                    {
                        AnsiConsole.MarkupLine(
                            $"[grey][[JSONPath]][/] 正在文件根应用 [blue]{Markup.Escape(options.RootPath!)}[/] ...");
                        parsedObjects = LoadAndApplyRootPath(options.InputPath, options.RootPath!);
                    }
                }
            }
            else
            {
                // 数据库直连：DbDataReader 惰性流式读取查询结果
                if (dbMode)
                {
                    parsedObjects = ParseFromDatabase(options);
                }
                // 如果指定了 --type，使用强类型反序列化路径
                else if (!string.IsNullOrEmpty(options.TypeFile))
                {
                    parsedObjects = RunTyped(options);
                }
                else
                {
                    parsedObjects = ParseByFormat(options, rules, effectiveValueParsers);
                }
            }

            // record-path 始终生效（即使在 root-path 之后）
            if (!string.IsNullOrWhiteSpace(options.RecordPath))
            {
                AnsiConsole.MarkupLine(
                    $"[grey][[JSONPath]][/] 正在每条记录上应用 [blue]{Markup.Escape(options.RecordPath!)}[/] ...");
                parsedObjects = ApplyRecordPathStream(parsedObjects, options.RecordPath!);
            }

            // 公共后处理：分析 → 报告 → 导出 → Schema 代码导出
            AnalysisResult result = ProcessAndReport(parsedObjects, options, rules, cancellationToken);

            int exitCode = 0;
            // CI 数据质量门禁：存在任一质量违规（枚举/范围/模式/必填/唯一/null 率/数据集期望）时以非零退出码结束
            if (options.FailOnViolations && result.QualityViolations.Count > 0)
            {
                long occurrenceCount = result.QualityViolations.Sum(v => v.Count);
                AnsiConsole.MarkupLine(
                    $"[red]数据质量未通过（{result.QualityViolations.Count} 条质量违规 / 涉及 {occurrenceCount} 行或值），退出码 3。[/]");
                foreach (var violation in result.QualityViolations.Take(5))
                {
                    string scope = string.IsNullOrEmpty(violation.Field)
                        ? QualityCheckTypes.Label(violation.Check)
                        : $"{violation.Field}: {QualityCheckTypes.Label(violation.Check)}";
                    AnsiConsole.MarkupLine($"  [red]·[/] {Markup.Escape(scope)} — {Markup.Escape(violation.Message)}");
                }

                if (result.QualityViolations.Count > 5)
                {
                    AnsiConsole.MarkupLine($"  [grey]... 还有 {result.QualityViolations.Count - 5} 条，详见 --violations 导出[/]");
                }

                exitCode = 3;
            }

            // CI 数据安全合规门禁：合规等级达到或劣于阈值时以非零退出码结束（与 --fail-on-violations 对称）
            if (exitCode == 0 && !string.IsNullOrWhiteSpace(options.FailOnPiiLevel))
            {
                var piiReport = PiiReport.Build(result);
                if (piiReport.PiiFieldCount > 0 &&
                    PiiSeverityRank(piiReport.ComplianceLevel) >= PiiSeverityRank(options.FailOnPiiLevel))
                {
                    AnsiConsole.MarkupLine(
                        $"[red]数据安全合规未通过（合规等级 {piiReport.ComplianceLevel}，发现 {piiReport.PiiFieldCount} 个敏感字段 / {piiReport.PiiValueCount} 个敏感值），退出码 4。[/]");
                    exitCode = 4;
                }
            }

            // CI 异常值门禁：任一数值字段异常值比例超阈值时以退出码 5 结束
            if (exitCode == 0 && options.FailOnOutlierRatio.HasValue)
            {
                double threshold = options.FailOnOutlierRatio.Value;
                var offenders = result.FieldStatistics
                    .Where(s => s.OutlierRatio.HasValue && s.OutlierRatio.Value > threshold)
                    .ToList();
                if (offenders.Count > 0)
                {
                    AnsiConsole.MarkupLine(
                        $"[red]异常值未通过（阈值 {threshold:P1}，发现 {offenders.Count} 个字段超限），退出码 5。[/]");
                    foreach (var s in offenders.Take(5))
                    {
                        AnsiConsole.MarkupLine(
                            $"  [red]·[/] [cyan]{Markup.Escape(s.FieldName)}[/]: {s.OutlierRatio:P1} (区间 {Markup.Escape(s.OutlierBounds ?? "-")}, {s.OutlierCount} 样本, {s.OutlierMethod} 近似)");
                    }
                    if (offenders.Count > 5)
                    {
                        AnsiConsole.MarkupLine($"  [grey]... 还有 {offenders.Count - 5} 个字段，详见报告[/]");
                    }
                    exitCode = 5;
                }
            }

            // CI Schema 演进门禁：--allow-breaking-schema-changes=false 时，含破坏性变更以退出码 6 结束
            if (exitCode == 0 && !options.AllowBreakingSchemaChanges && result.SchemaDiff != null && result.SchemaDiff.HasBreakingChanges)
            {
                int removed = result.SchemaDiff.RemovedFields.Count;
                int breakingChanges = result.SchemaDiff.ChangedFields.Count(c => c.Breaking);
                AnsiConsole.MarkupLine(
                    $"[red]Schema 演进门禁未通过（--allow-breaking-schema-changes=false，发现 {removed} 个删除字段、{breakingChanges} 个破坏性变更字段），退出码 6。[/]");
                foreach (var f in result.SchemaDiff.RemovedFields.Take(5))
                {
                    AnsiConsole.MarkupLine(
                        $"  [red]-[/] [cyan]{Markup.Escape(f.Path)}[/]: {Markup.Escape(f.PrimaryType)} (coverage {f.Coverage:P1})");
                }
                foreach (var c in result.SchemaDiff.ChangedFields.Where(c => c.Breaking).Take(5))
                {
                    AnsiConsole.MarkupLine($"  [red]~[/] [cyan]{Markup.Escape(c.Path)}[/]");
                    foreach (var item in c.Changes)
                    {
                        if (item.Breaking)
                        {
                            AnsiConsole.MarkupLine($"      [red]·[/] {Markup.Escape(item.Property)}: {Markup.Escape(item.Description)}");
                        }
                    }
                }
                exitCode = 6;
            }

            // 成功 / 业务门禁失败统一记录 analysis_finish（含退出码与结果摘要）
            string finishEvent = exitCode == 0 ? "analysis_finish" : "analysis_finish";
            string? finishMsg = exitCode == 0
                ? null
                : exitCode switch
                {
                    3 => "数据质量门禁未通过",
                    4 => "数据安全合规门禁未通过",
                    5 => "异常值门禁未通过",
                    6 => "Schema 演进门禁未通过",
                    _ => null
                };
            WriteAuditEvent(audit, options, rules, runId, sw, "analysis_finish", exitCode, result, finishMsg);
            return exitCode;
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[yellow]分析已取消。[/]");
            WriteAuditEvent(audit, options, rules, runId, sw, "analysis_cancel", exitCode: 2, result: null, message: "用户取消");
            return 2;
        }
        catch (Exception ex)
        {
            PrintError(ex);
            WriteAuditEvent(audit, options, rules, runId, sw, "analysis_error", exitCode: 1, result: null, message: ex.Message);
            return 1;
        }
    }

    private static void WriteAuditStart(AuditLogger? audit, AnalysisOptions options, AnalysisRules? rules, string runId)
    {
        if (audit == null) return;
        audit.Write(new AuditLogEntry
        {
            Timestamp = AuditLogger.FormatTimestamp(DateTime.UtcNow),
            Event = "analysis_start",
            RunId = runId,
            Operator = string.IsNullOrWhiteSpace(options.Operator) ? "anonymous" : options.Operator,
            ToolVersion = GetToolVersion(),
            InputSource = BuildInputSource(options),
            InputHash = AuditLogger.ComputeFileHash(options.InputPath),
            InputBytes = AuditLogger.GetFileSize(options.InputPath),
            Options = AuditLogger.Snapshot(options, rules)
        });
    }

    private static void WriteAuditEvent(
        AuditLogger? audit,
        AnalysisOptions options,
        AnalysisRules? rules,
        string runId,
        StopwatchLike sw,
        string eventName,
        int exitCode,
        AnalysisResult? result,
        string? message)
    {
        if (audit == null) return;
        audit.Write(new AuditLogEntry
        {
            Timestamp = AuditLogger.FormatTimestamp(DateTime.UtcNow),
            Event = eventName,
            RunId = runId,
            Operator = string.IsNullOrWhiteSpace(options.Operator) ? "anonymous" : options.Operator,
            ToolVersion = GetToolVersion(),
            InputSource = BuildInputSource(options),
            InputHash = AuditLogger.ComputeFileHash(options.InputPath),
            InputBytes = AuditLogger.GetFileSize(options.InputPath),
            Options = AuditLogger.Snapshot(options, rules),
            Result = result == null ? null : AuditLogger.Summarize(result, sw),
            Message = message,
            ExitCode = exitCode
        });
    }

    private static string BuildInputSource(AnalysisOptions options)
    {
        if (!string.IsNullOrEmpty(options.ConnectionString))
        {
            string provider = string.IsNullOrEmpty(options.DbProvider) ? "auto" : options.DbProvider;
            string query = options.Query ?? "";
            return $"db://{provider}?{query}";
        }
        return options.InputPath;
    }

    private static string GetToolVersion()
    {
        try
        {
            var asm = typeof(AnalysisRunner).Assembly;
            var info = asm.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false);
            if (info.Length > 0)
            {
                return ((System.Reflection.AssemblyInformationalVersionAttribute)info[0]).InformationalVersion;
            }
            return asm.GetName().Version?.ToString() ?? "unknown";
        }
        catch
        {
            return "unknown";
        }
    }

    /// <summary>
    /// Detects format from file extension and parses the file accordingly.
    /// Uses the parser registry in FileParsers (IFileParser pattern).
    /// </summary>
    private static IEnumerable<Dictionary<string, object?>> ParseByFormat(
        AnalysisOptions options,
        AnalysisRules? rules = null,
        IReadOnlyList<ValueParserConfig>? valueParsers = null)
    {
        string format = ResolveEffectiveFormat(options, rules);

        if (RequiresExcelJsonValueParsing(rules))
        {
            AnsiConsole.MarkupLine($"[grey][[规则驱动]][/] 因规则文件配置了 valueParsers，使用格式 '[blue]{format}[/]' 进行解析。");
        }
        else
        {
            AnsiConsole.MarkupLine($"[grey][[自动检测]][/] 使用格式 '[blue]{format}[/]' 进行解析。");
        }

        AnsiConsole.MarkupLine($"正在读取并解析文件: [blue]{Markup.Escape(options.InputPath)}[/] ...");

        var compression = CompressionDetector.Detect(options.InputPath);
        if (compression != CompressionType.None)
        {
            string label = compression switch
            {
                CompressionType.GZip => "gzip",
                CompressionType.BZip2 => "bzip2",
                CompressionType.Zip => "zip",
                _ => "压缩"
            };
            AnsiConsole.MarkupLine($"[grey][[自动解压]][/] 检测到 {label} 压缩文件，正在流式解压后解析（保持行级流式，超大文件内存有界）...");

            // zip 含多个数据条目时提示确定性选择结果，避免“为什么统计的是这个文件”的困惑
            if (compression == CompressionType.Zip &&
                InputSource.PreviewZipEntries(options.InputPath) is { } preview &&
                preview.Others.Count > 0)
            {
                AnsiConsole.MarkupLine(
                    $"[grey][[自动解压]][/] zip 含 {preview.Others.Count + 1} 个数据条目，已选择 [blue]{Markup.Escape(preview.Chosen)}[/]（其余条目忽略）");
            }
        }
        var parseOptions = new ParseOptions
        {
            Flatten = ResolveFlatten(options, rules),
            ValueParsers = valueParsers ?? Array.Empty<ValueParserConfig>(),
            SampleReservoir = options.SampleReservoir,
            SampleStep = options.SampleStep
        };
        IEnumerable<Dictionary<string, object?>> parsed = FileParsers.ParseByFormat(options.InputPath, format, parseOptions);

        // jsonl / csv / excel / excel-json 已在行/记录级跳过未选中记录；
        // 其余整体物化格式（json / xml / yaml）在解析完成后统一做采样包装。
        bool parserAlreadySampled = format is "jsonl" or "csv" or "excel" or "excel-json";
        return ApplySamplingIfEnabled(parsed, options, parserAlreadySampled);
    }

    private static string ResolveEffectiveFormat(AnalysisOptions options, AnalysisRules? rules)
    {
        if (RequiresExcelJsonValueParsing(rules))
        {
            return "excel-json";
        }

        return FileParsers.DetectFormat(options.InputPath);
    }

    private static bool RequiresExcelJsonValueParsing(AnalysisRules? rules)
    {
        return rules?.ValueParsers is { Count: > 0 };
    }

    private static IEnumerable<Dictionary<string, object?>> ParseFromDatabase(AnalysisOptions options)
    {
        var provider = DatabaseReader.ResolveProvider(options.DbProvider, options.ConnectionString!);
        AnsiConsole.MarkupLine($"[grey][[数据库直连]][/] 使用 [blue]{DatabaseReader.DisplayName(provider)}[/] 数据源，流式读取查询结果...");
        AnsiConsole.MarkupLine($"正在直连数据库并执行查询: [blue]{Markup.Escape(options.Query!)}[/] ...");

        IEnumerable<Dictionary<string, object?>> parsed =
            DatabaseReader.ReadRows(options.ConnectionString!, provider, options.Query!);

        return ApplySamplingIfEnabled(parsed, options, parserAlreadySampled: false);
    }

    private static IEnumerable<Dictionary<string, object?>> ApplySamplingIfEnabled(
        IEnumerable<Dictionary<string, object?>> parsed,
        AnalysisOptions options,
        bool parserAlreadySampled)
    {
        if (!Sampling.IsEnabled(options.SampleReservoir, options.SampleStep))
            return parsed;

        AnnounceSampling(options);
        return parserAlreadySampled
            ? parsed
            : Sampling.Apply(parsed, options.SampleReservoir, options.SampleStep);
    }

    private static void AnnounceSampling(AnalysisOptions options)
    {
        if (options.SampleReservoir > 0)
        {
            AnsiConsole.MarkupLine($"[grey][[采样]][/] 蓄水池随机抽样：从海量数据中均匀抽取最多 [blue]{options.SampleReservoir}[/] 条进行统计与 Schema 概览。");
        }
        else
        {
            AnsiConsole.MarkupLine($"[grey][[采样]][/] 等距系统抽样：每 [blue]{options.SampleStep}[/] 条记录取 1 条进行统计与 Schema 概览。");
        }
    }

    /// <summary>
    /// Compiles the type definition file and performs strongly-typed deserialization.
    /// </summary>
    private static List<Dictionary<string, object?>> RunTyped(AnalysisOptions options)
    {
        AnsiConsole.MarkupLine($"正在编译类型定义文件: [blue]{Markup.Escape(options.TypeFile!)}[/] ...");
        var compileResult = TypeLoader.Compile(options.TypeFile!, options.TypeName);

        var targetType = compileResult.type;
        AnsiConsole.MarkupLine($"编译成功。目标类型: [green]{targetType.FullName}[/]");

        AnsiConsole.MarkupLine($"正在使用强类型反序列化文件: [blue]{Markup.Escape(options.InputPath)}[/] ...");
        using var typedInput = InputSource.Open(options.InputPath);
        var typedResult = TypedFileParser.ParseJson(typedInput.Content, targetType);

        var materialized = new List<Dictionary<string, object?>>();
        try
        {
            materialized.AddRange(typedResult.Objects);

            if (typedResult.Errors.Count > 0)
            {
                foreach (var err in typedResult.Errors.Take(5))
                {
                    AnsiConsole.MarkupLine($"[yellow]属性读取警告: {Markup.Escape(err)}[/]");
                }
                if (typedResult.Errors.Count > 5)
                {
                    AnsiConsole.MarkupLine($"[yellow]... 还有 {typedResult.Errors.Count - 5} 条读取错误[/]");
                }
            }
        }
        finally
        {
            compileResult.context.Dispose();
        }

        // 强类型路径已整体物化，采样以通用包装实现
        if (Sampling.IsEnabled(options.SampleReservoir, options.SampleStep))
        {
            AnnounceSampling(options);
            return Sampling.Apply(materialized, options.SampleReservoir, options.SampleStep).ToList();
        }

        return materialized;
    }

    /// <summary>
    /// Common post-processing: analyze objects, print console report, export reports and optional schema.
    /// </summary>
    private static AnalysisResult ProcessAndReport(
        IEnumerable<Dictionary<string, object?>> parsedObjects,
        AnalysisOptions options,
        AnalysisRules? rules = null,
        CancellationToken cancellationToken = default)
    {
        // 规则已在 Run() 中提前加载；如调用方未提供，则按需加载
        rules ??= !string.IsNullOrEmpty(options.RulesFile) ? LoadRules(options.RulesFile) : null;

        // 过滤条件：--where 优先，其次规则文件 filter
        string? effectiveFilter = !string.IsNullOrWhiteSpace(options.Filter)
            ? options.Filter
            : !string.IsNullOrWhiteSpace(rules?.Filter) ? rules.Filter : null;

        // 字段选择 + 值转换 + 过滤（--fields / 规则文件 selectedFields / transform / --where / 规则文件 filter）
        var transformProcessor = new FieldTransformProcessor(rules, SplitFields(options.Fields), effectiveFilter);
        if (effectiveFilter != null)
        {
            AnsiConsole.MarkupLine($"[grey][[过滤]][/] 已应用过滤条件: [blue]{Markup.Escape(effectiveFilter)}[/]");
        }
        if (transformProcessor.HasSelectionOrTransforms)
        {
            parsedObjects = transformProcessor.Wrap(parsedObjects);
        }

        // 模糊去重需要多次枚举解析后对象（在 Analyze 之前完成）。
        // 物化一次既保证模糊去重的随机访问、也避免 Analyzer 重复解析；
        // 数据量 < 1MB 时物化成本可忽略；后续如需扩展可加 --fuzzy-dedup-stream 改为单遍流式。
        bool needFuzzyDedup = !string.IsNullOrWhiteSpace(options.FuzzyDedupField);
        bool needDistributionSketches =
            !string.IsNullOrEmpty(options.DistributionSnapshotPath) ||
            !string.IsNullOrEmpty(options.DiffDistributionBaseline);
        List<Dictionary<string, object?>> materialized = (needFuzzyDedup || needDistributionSketches)
            ? parsedObjects.ToList()
            : new List<Dictionary<string, object?>>();

        // 模糊去重（仅在 --fuzzy-dedup-field 指定时执行）
        FuzzyDuplicateReport? fuzzyReport = null;
        if (needFuzzyDedup)
        {
            string field = options.FuzzyDedupField!;
            AnsiConsole.MarkupLine($"[grey][[模糊去重]][/] 正在对字段 [blue]{Markup.Escape(field)}[/] 做形态变体聚类...");
            fuzzyReport = FuzzyDuplicateDetector.Detect(materialized, obj =>
            {
                if (obj.TryGetValue(field, out var v)) return v?.ToString();
                return null;
            });
        }

        AnalysisResult result = null!;

        // P6.1：进度条 / ETA 集成。
        // 优先用"已知总量"驱动 maxValue（materialized 列表直接用 Count；其余场景不强行预扫描——
        // 50GB JSONL 再读一遍数行需要 30+ 秒，得不偿失）；
        // 未知时退化为纯对象计数 + spinner，RemainingTimeColumn 仍可基于速率给出粗略 ETA。
        long? expectedTotal = options.ShowProgress ? EstimateTotalObjects(materialized) : null;

        if (options.ShowProgress)
        {
            AnsiConsole.Progress()
                .AutoClear(false)
                .HideCompleted(false)
                .Columns(
                    new TaskDescriptionColumn { Alignment = Justify.Left },
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new RemainingTimeColumn(),
                    new SpinnerColumn(Spinner.Known.Dots))
                .Start(ctx =>
                {
                    string desc = string.IsNullOrEmpty(options.InputPath)
                        ? "[green]分析中[/] [grey](数据库直连)[/]"
                        : $"[green]分析中[/] [grey]{Markup.Escape(Path.GetFileName(options.InputPath))}[/]";
                    var task = ctx.AddTask(desc, maxValue: expectedTotal ?? 100);
                    var progress = new Progress<long>(objects => task.Value = objects);

                    var parallelOptions = new ParallelOptions
                    {
                        MaxDegreeOfParallelism = options.MaxDegreeOfParallelism > 0
                            ? options.MaxDegreeOfParallelism
                            : -1
                    };
                    var features = BuildAnalysisFeatures(options);
                    if (needDistributionSketches)
                    {
                        var detailed = ObjectAnalyzer.AnalyzeDetailed(materialized, rules, parallelOptions, features, progress, cancellationToken);
                        result = detailed.Result;
                        _lastQuantileSketches = detailed.QuantileSketches;
                        _lastDistinctSketches = detailed.DistinctSketches;
                    }
                    else
                    {
                        result = ObjectAnalyzer.Analyze(parsedObjects, rules, parallelOptions, features, progress, cancellationToken);
                        _lastQuantileSketches = null;
                        _lastDistinctSketches = null;
                    }
                    // 确保 100% 收尾（未知总量时 task.Value 可能停在 < maxValue 处）
                    if (task.Value < task.MaxValue) task.Value = task.MaxValue;
                });
        }
        else
        {
            AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(Style.Parse("green"))
                .Start("正在并行计算对象字段分布、数据极值与语义模式...", ctx =>
                {
                    var parallelOptions = new ParallelOptions
                    {
                        MaxDegreeOfParallelism = options.MaxDegreeOfParallelism > 0
                            ? options.MaxDegreeOfParallelism
                            : -1
                    };
                    var features = BuildAnalysisFeatures(options);
                    if (needDistributionSketches)
                    {
                        var detailed = ObjectAnalyzer.AnalyzeDetailed(materialized, rules, parallelOptions, features, progress: null, cancellationToken);
                        result = detailed.Result;
                        _lastQuantileSketches = detailed.QuantileSketches;
                        _lastDistinctSketches = detailed.DistinctSketches;
                    }
                    else
                    {
                        result = ObjectAnalyzer.Analyze(parsedObjects, rules, parallelOptions, features, progress: null, cancellationToken);
                        _lastQuantileSketches = null;
                        _lastDistinctSketches = null;
                    }
                });
        }

        // 模糊去重结果挂到 result 上
        if (fuzzyReport != null) result.FuzzyDuplicates = fuzzyReport;

        // 采样置信区间：仅当启用 sampling 时计算（对每字段覆盖率做 Wilson 95% 评估）
        if (Sampling.IsEnabled(options.SampleReservoir, options.SampleStep))
        {
            long effective = options.SampleReservoir > 0
                ? options.SampleReservoir
                : Math.Max(1, result.TotalObjects / Math.Max(1, options.SampleStep));
            long estimated = result.TotalObjects;
            var fieldCounts = result.FieldStatistics
                .Select(s => (s.FieldName, s.Count));
            result.SamplingConfidence = SamplingConfidenceCalculator.Build(
                method: options.SampleReservoir > 0 ? "reservoir" : "systematic",
                sampleSize: effective,
                estimatedTotal: estimated,
                fieldCounts: fieldCounts,
                total: estimated);
        }

        // 转换失败次数与转换类型回填到统计结果
        transformProcessor.ApplyFailures(result);
        result.FilterExpression = effectiveFilter;

        AnsiConsole.MarkupLine("[green]分析完成！[/]\n");

        // Schema 快照导出 / diff 计算（必须在 ConsoleReporter.Print 与 ReportExporter.Save 之前，
        // 这样控制台与 HTML/MD/JSON 报告的「Schema 演进」小节才能带 diff 数据）
        ProcessSchemaSnapshot(result, options);

        // 分布快照导出 / diff 计算（与 SchemaSnapshot 互补：看分布变化）
        ProcessDistributionSnapshot(result, options);

        // 输出控制台统计结果（--pii-off 时保留原始值）
        ConsoleReporter.Print(result, options.MaxValuesToShow, redactPii: !options.PiiOff);

        // 输出数据安全合规摘要（发现敏感字段时提示打码与合规导出；--pii-off 时跳过）
        if (!options.PiiOff)
        {
            PiiReport.PrintSummary(result);
        }

        // 如果指定了输出路径，则保存详细报告
        if (!string.IsNullOrEmpty(options.OutputPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReportExporter.Save(result, options.OutputPath, redactPii: !options.PiiOff);
        }

        // 如果指定了违规导出路径，则单独导出枚举违规清单
        if (!string.IsNullOrEmpty(options.ViolationsPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ViolationExporter.Save(result, options.ViolationsPath, redactPii: !options.PiiOff);
        }

        // 如果指定了数据质量报告路径，导出结构化 Pass/Fail 质量报告
        if (!string.IsNullOrEmpty(options.QualityPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            QualityExporter.Save(result, options.QualityPath, redactPii: !options.PiiOff);
        }

        // 如果指定了数据安全合规报告路径，导出合规等级 + 敏感字段清单（--pii-off 时跳过）
        if (!string.IsNullOrEmpty(options.PiiPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            PiiExporter.Save(result, options.PiiPath);
        }

        // 如果指定了 Schema 导出，自动生成 C# / TypeScript / JSON Schema 定义
        if (!string.IsNullOrEmpty(options.ExportSchema))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string basePath = options.OutputPath ?? options.InputPath;
            if (string.IsNullOrEmpty(basePath)) basePath = "database"; // 数据库直连模式下没有输入文件
            string schemaExt = options.ExportSchema.ToLowerInvariant() switch
            {
                "csharp" or "cs" => ".schema.cs",
                "typescript" or "ts" => ".schema.d.ts",
                "openapi" or "swagger" or "openapi-json" => ".openapi.json",
                "openapi-yaml" or "yaml-schema" => ".openapi.yaml",
                "protobuf" or "proto" or "proto3" => ".schema.proto",
                "sql" or "postgres" or "postgresql" or "pg" or "mysql" or "sqlite" or "sqlserver" or "mssql" => ".schema.sql",
                _ => ".schema.json"
            };
            string schemaPath = options.SchemaOutputPath ?? Path.Combine(
                Path.GetDirectoryName(basePath) ?? "",
                Path.GetFileNameWithoutExtension(basePath) + schemaExt);

            AnsiConsole.MarkupLine($"正在导出 [bold green]{options.ExportSchema}[/] 格式 Schema 定义至: [blue]{Markup.Escape(schemaPath)}[/] ...");
            SchemaExporter.Export(result, options.ExportSchema, schemaPath);
            AnsiConsole.MarkupLine("[green]Schema 定义代码成功导出！[/]");
        }

        // Schema 快照导出 / diff 计算（独立于代码生成器 SchemaExporter）
        // 已在 ProcessAndReport 前置阶段完成（见 ProcessAndReport 顶部），此处保留注释便于阅读。
        return result;
    }

    /// <summary>
    /// 从 CLI 选项构建 <see cref="AnalysisFeatures"/>：当用户未启用任何 P4 特性时返回 null，
    /// 以避免默认路径上的任何额外开销（与 v1.x 行为兼容）。
    /// </summary>
    internal static AnalysisFeatures? BuildAnalysisFeatures(AnalysisOptions options)
    {
        bool anyEnabled = options.EnableCorrelation
            || options.EnableStringPatterns
            || options.EnableUnicodeHygiene
            || options.EnableTimeSeries
            || !string.IsNullOrEmpty(options.DistributionSnapshotPath)
            || !string.IsNullOrEmpty(options.DiffDistributionBaseline);
        if (!anyEnabled) return null;
        return new AnalysisFeatures
        {
            EnableCorrelation = options.EnableCorrelation,
            EnableStringPatterns = options.EnableStringPatterns,
            EnableUnicodeHygiene = options.EnableUnicodeHygiene,
            EnableTimeSeries = options.EnableTimeSeries,
            EnableDistributionSnapshot = !string.IsNullOrEmpty(options.DistributionSnapshotPath)
                || !string.IsNullOrEmpty(options.DiffDistributionBaseline)
        };
    }

    /// <summary>
    /// 处理 Schema 快照导出（--export-schema-snapshot）与 diff 计算（--diff-schema）。
    /// 失败抛 FileNotFoundException / InvalidDataException，由调用方转为退出码 1。
    /// </summary>
    private static void ProcessSchemaSnapshot(AnalysisResult result, AnalysisOptions options)
    {
        bool needBuildSnapshot =
            !string.IsNullOrEmpty(options.SchemaSnapshotPath) ||
            !string.IsNullOrEmpty(options.DiffSchemaBaseline) ||
            !string.IsNullOrEmpty(options.SchemaDiffReportPath);

        if (!needBuildSnapshot)
            return;

        // 来源标识：优先输入文件路径；其次数据库直连描述；最后空（snapshot 中省略）
        string? source = !string.IsNullOrEmpty(options.InputPath)
            ? options.InputPath
            : (!string.IsNullOrEmpty(options.ConnectionString)
                ? $"db://{(string.IsNullOrEmpty(options.DbProvider) ? "auto" : options.DbProvider)}?{options.Query ?? ""}"
                : null);

        SchemaSnapshot? currentSnapshot = null;
        if (!string.IsNullOrEmpty(options.SchemaSnapshotPath))
        {
            currentSnapshot = SchemaSnapshotExporter.Build(result, source);
            // SchemaSnapshotExporter.Save 内部已打印成功日志
            string? dir = Path.GetDirectoryName(options.SchemaSnapshotPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            SchemaSnapshotExporter.Save(result, options.SchemaSnapshotPath, source);
        }

        if (!string.IsNullOrEmpty(options.DiffSchemaBaseline))
        {
            var baseline = SchemaDiffer.LoadFromFile(options.DiffSchemaBaseline!);
            // 优先用刚导出的快照（确保与磁盘上的 baseline 比较时内容完全一致），否则现场构建
            currentSnapshot ??= SchemaSnapshotExporter.Build(result, source);
            var diff = SchemaDiffer.Compute(baseline, currentSnapshot);
            result.SchemaDiff = diff;

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold]── Schema 演进（vs 基线） ──[/]");
            SchemaDiffer.PrintSummary(diff);

            if (!string.IsNullOrEmpty(options.SchemaDiffReportPath))
            {
                string? dir = Path.GetDirectoryName(options.SchemaDiffReportPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                File.WriteAllText(options.SchemaDiffReportPath, JsonSerializer.Serialize(diff, s_diffJsonOptions), new UTF8Encoding(false));
                AnsiConsole.MarkupLine(
                    $"[green]Schema diff 已导出至[/] [blue]{Markup.Escape(options.SchemaDiffReportPath)}[/]");
            }
        }
    }

    /// <summary>
    /// 处理分布快照导出（--export-distribution-snapshot）与分布 diff 计算（--diff-distribution）。
    /// 与 <see cref="ProcessSchemaSnapshot"/> 互补：后者看结构变化，本方法看分布变化（覆盖率 / 均值 / 分位 / distinct）。
    /// 失败抛 FileNotFoundException / InvalidDataException，由调用方转为退出码 1。
    /// </summary>
    private static void ProcessDistributionSnapshot(AnalysisResult result, AnalysisOptions options)
    {
        bool needSnapshot =
            !string.IsNullOrEmpty(options.DistributionSnapshotPath) ||
            !string.IsNullOrEmpty(options.DiffDistributionBaseline) ||
            !string.IsNullOrEmpty(options.DistributionDiffReportPath);

        if (!needSnapshot)
            return;

        // 来源标识：与 SchemaSnapshot 同一口径
        string? source = !string.IsNullOrEmpty(options.InputPath)
            ? options.InputPath
            : (!string.IsNullOrEmpty(options.ConnectionString)
                ? $"db://{(string.IsNullOrEmpty(options.DbProvider) ? "auto" : options.DbProvider)}?{options.Query ?? ""}"
                : null);

        DistributionSnapshot? currentSnapshot = null;
        if (!string.IsNullOrEmpty(options.DistributionSnapshotPath))
        {
            currentSnapshot = DistributionSnapshotExporter.Build(result, _lastQuantileSketches, _lastDistinctSketches, source);
            DistributionSnapshotExporter.WriteToFile(currentSnapshot, options.DistributionSnapshotPath);
            AnsiConsole.MarkupLine(
                $"[green]分布快照已导出至[/] [blue]{Markup.Escape(options.DistributionSnapshotPath)}[/] [grey]({currentSnapshot.Fields.Count} 字段)[/]");
        }

        if (!string.IsNullOrEmpty(options.DiffDistributionBaseline))
        {
            var baseline = DistributionSnapshotExporter.LoadFromFile(options.DiffDistributionBaseline!);
            currentSnapshot ??= DistributionSnapshotExporter.Build(result, _lastQuantileSketches, _lastDistinctSketches, source);
            var diff = DistributionDiffer.Compute(baseline, currentSnapshot);
            result.DistributionDiff = diff;

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold]── 分布漂移（vs 基线） ──[/]");
            DistributionDiffer.PrintSummary(diff);

            if (!string.IsNullOrEmpty(options.DistributionDiffReportPath))
            {
                string? dir = Path.GetDirectoryName(options.DistributionDiffReportPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                File.WriteAllText(options.DistributionDiffReportPath, JsonSerializer.Serialize(diff, s_diffJsonOptions), new UTF8Encoding(false));
                AnsiConsole.MarkupLine(
                    $"[green]分布 diff 已导出至[/] [blue]{Markup.Escape(options.DistributionDiffReportPath)}[/]");
            }
        }
    }

    private static void PrintError(Exception ex)
    {
        AnsiConsole.MarkupLine($"[red]发生错误: {Markup.Escape(ex.Message)}[/]");
        if (ex.InnerException != null)
        {
            AnsiConsole.MarkupLine($"[red]内部错误: {Markup.Escape(ex.InnerException.Message)}[/]");
        }
    }

    /// <summary>
    /// P6.1：估算分析对象总数（用于 Progress bar 的 maxValue / ETA）。
    /// 策略：
    /// 1) 已物化的列表（fuzzy dedup / distribution sketches 路径）→ 直接返回 Count；
    /// 2) 其它路径返回 null，让 Progress 显示纯对象计数 + 速率，RemainingTimeColumn 仍可估算。
    /// 不对 50GB JSONL 做二次扫描数行——开销得不偿失。
    /// </summary>
    private static long? EstimateTotalObjects(List<Dictionary<string, object?>> materialized)
    {
        if (materialized != null && materialized.Count > 0)
        {
            return materialized.Count;
        }
        return null;
    }

    /// <summary>合规等级严重度排序：A=0（干净）&lt; B=1（敏感已打码）&lt; C=2（需人工复核）。</summary>
    private static int PiiSeverityRank(string level) => level switch
    {
        "C" => 2,
        "B" => 1,
        _ => 0
    };

    private static AnalysisRules LoadRules(string rulesPath)
    {
        if (!File.Exists(rulesPath))
        {
            throw new FileNotFoundException($"规则文件不存在: {rulesPath}");
        }

        AnsiConsole.MarkupLine($"正在加载规则文件: [blue]{Markup.Escape(rulesPath)}[/] ...");
        var json = File.ReadAllText(rulesPath);
        var rules = JsonSerializer.Deserialize<AnalysisRules>(json);
        if (rules == null)
        {
            throw new InvalidOperationException($"规则文件解析失败: {rulesPath}");
        }

        // 质量检查 v2：pattern 预编译验证，无效正则 fail-fast（而非分析完成后才报错）
        foreach (var field in rules.Fields)
        {
            if (string.IsNullOrEmpty(field.Value.Pattern))
                continue;
            try
            {
                QualityChecks.CompilePattern(field.Value.Pattern!);
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException(
                    $"字段 [blue]{field.Key}[/] 的 pattern 正则无效: {ex.Message}");
            }
        }

        AnsiConsole.MarkupLine($"已加载 [green]{rules.Fields.Count}[/] 条字段规则。");
        return rules;
    }

    private static bool ResolveFlatten(AnalysisOptions options, AnalysisRules? rules)
    {
        // --record-path 必须看到嵌套子对象才能正确投影；提前展平会让 $.foo.bar 这种路径找不到目标。
        // 因此启用 --record-path 时，强制不展平（让 record-path 处理完后再分析，提取出的子对象本身就是平坦的）。
        if (!string.IsNullOrWhiteSpace(options.RecordPath))
            return false;

        if (!options.Flatten)
            return false;

        if (rules?.Flatten is bool flatten)
            return flatten;

        return options.Flatten;
    }

    private static IReadOnlyList<ValueParserConfig> ResolveValueParsers(AnalysisOptions options, AnalysisRules? rules)
    {
        if (rules?.ValueParsers is { Count: > 0 })
        {
            return rules.ValueParsers
                .Where(p => !string.IsNullOrWhiteSpace(p.Source))
                .Select(p => new ValueParserConfig
                {
                    Source = p.Source.Trim(),
                    ParseType = string.IsNullOrWhiteSpace(p.ParseType) ? "auto" : p.ParseType.Trim(),
                    Flatten = p.Flatten,
                    Header = p.Header
                })
                .ToList();
        }

        return Array.Empty<ValueParserConfig>();
    }

    private static List<string>? SplitFields(string? fields)
    {
        if (string.IsNullOrWhiteSpace(fields))
            return null;
        return fields.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<Dictionary<string, object?>> LoadAndApplyRootPath(string path, string expression)
    {
        // 整体读入 JSON：JSONPath 投影必须访问整个文档，无法流式。
        // 仅当输入是 JSON 整体文件时才走这条路径（其他格式已在调用方过滤）。
        byte[] bytes;
        using (var fs = File.OpenRead(path))
        using (var ms = new MemoryStream())
        {
            fs.CopyTo(ms);
            bytes = ms.ToArray();
        }
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            bytes = bytes.AsSpan(3).ToArray();
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(bytes);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("JSON 根节点解析失败。", ex);
        }

        var records = JsonPathProjector.ApplyRootPath(root, expression);
        if (records.Count == 0)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]警告: --root-path 表达式 {Markup.Escape(expression)} 匹配到 0 个节点；将分析空集[/]");
        }
        else
        {
            AnsiConsole.MarkupLine(
                $"[green][[JSONPath]][/] --root-path 提取了 [blue]{records.Count}[/] 条记录");
        }
        return records;
    }

    /// <summary>
    /// 流式地把 record-path 应用到每条记录上：每条记录可能 fan-out 为多条，因此这里只能把 source 物化为 list；
    /// 调用方传入的 source 在大多数解析路径上（jsonl/csv/excel/db/typed）已经是 list，因此实际成本可控。
    /// </summary>
    private static List<Dictionary<string, object?>> ApplyRecordPathStream(
        IEnumerable<Dictionary<string, object?>> source,
        string expression)
    {
        var materialized = source as IReadOnlyList<Dictionary<string, object?>> ?? source.ToList();
        var projected = new List<Dictionary<string, object?>>(materialized.Count);
        int noMatch = 0;
        foreach (var record in materialized)
        {
            var matches = JsonPathProjector.ApplyRecordPath(record, expression);
            if (matches.Count == 0)
            {
                noMatch++;
                continue;
            }
            projected.AddRange(matches);
        }

        if (noMatch > 0)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]警告: --record-path 表达式 {Markup.Escape(expression)} 在 {noMatch}/{materialized.Count} 条记录上无匹配；这些记录被跳过[/]");
        }
        if (projected.Count == 0 && materialized.Count > 0)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]警告: --record-path 匹配到 0 个节点；将分析空集[/]");
        }
        else
        {
            AnsiConsole.MarkupLine(
                $"[green][[JSONPath]][/] --record-path 产出 [blue]{projected.Count}[/] 条记录（输入 {materialized.Count} 条）");
        }
        return projected;
    }
}
