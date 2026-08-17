using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using Spectre.Console;

namespace ObjectAnalyzer.Analysis;

public static class AnalysisRunner
{
    /// <summary>
    /// Runs the analysis pipeline. Returns 0 on success, 1 on failure, 2 on cancellation.
    /// </summary>
    public static int Run(AnalysisOptions options, CancellationToken cancellationToken = default)
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
            rules = LoadRules(options.RulesFile);
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

        try
        {
            IEnumerable<Dictionary<string, object?>> parsedObjects;
            var effectiveValueParsers = ResolveValueParsers(options, rules);

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

            // 公共后处理：分析 → 报告 → 导出 → Schema 代码导出
            AnalysisResult result = ProcessAndReport(parsedObjects, options, rules, cancellationToken); // CI 数据质量门禁：存在枚举违规时以非零退出码结束
            if (options.FailOnViolations && result.FieldStatistics.Any(f => f.EnumViolations.Count > 0))
            {
                int violationCount = result.FieldStatistics.Sum(f => f.EnumViolations.Count);
                AnsiConsole.MarkupLine($"[red]数据质量未通过（共 {violationCount} 条枚举违规），退出码 3。[/]");
                return 3;
            }

            return 0;
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[yellow]分析已取消。[/]");
            return 2;
        }
        catch (Exception ex)
        {
            PrintError(ex);
            return 1;
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
        string? format = options.Format;

        // 自动检测文件格式
        if (string.IsNullOrEmpty(format))
        {
            format = FileParsers.DetectFormat(options.InputPath);
            AnsiConsole.MarkupLine($"[grey][[自动检测]][/] 使用格式 '[blue]{format}[/]' 进行解析。");
        }

        AnsiConsole.MarkupLine($"正在读取并解析文件: [blue]{Markup.Escape(options.InputPath)}[/] ...");
        var parseOptions = new ParseOptions
        {
            Flatten = ResolveFlatten(options, rules),
            ExcelJsonColumn = !string.IsNullOrWhiteSpace(options.ExcelJsonColumn)
                ? options.ExcelJsonColumn
                : "A",
            ExcelJsonColumns = Array.Empty<string>(),
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
        var typedResult = TypedFileParser.ParseJson(options.InputPath, targetType);

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

        AnalysisResult result = null!;
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
                result = ObjectAnalyzer.Analyze(parsedObjects, rules, parallelOptions, cancellationToken);
            });

        // 转换失败次数与转换类型回填到统计结果
        transformProcessor.ApplyFailures(result);
        result.FilterExpression = effectiveFilter;

        AnsiConsole.MarkupLine("[green]分析完成！[/]\n");

        // 输出控制台统计结果
        ConsoleReporter.Print(result, options.MaxValuesToShow);

        // 输出数据安全合规摘要（发现敏感字段时提示打码与合规导出）
        PiiReport.PrintSummary(result);

        // 如果指定了输出路径，则保存详细报告
        if (!string.IsNullOrEmpty(options.OutputPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReportExporter.Save(result, options.OutputPath);
        }

        // 如果指定了违规导出路径，则单独导出枚举违规清单
        if (!string.IsNullOrEmpty(options.ViolationsPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ViolationExporter.Save(result, options.ViolationsPath);
        }

        // 如果指定了数据质量报告路径，导出结构化 Pass/Fail 质量报告
        if (!string.IsNullOrEmpty(options.QualityPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            QualityExporter.Save(result, options.QualityPath);
        }

        // 如果指定了数据安全合规报告路径，导出合规等级 + 敏感字段清单
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

        return result;
    }

    private static void PrintError(Exception ex)
    {
        AnsiConsole.MarkupLine($"[red]发生错误: {Markup.Escape(ex.Message)}[/]");
        if (ex.InnerException != null)
        {
            AnsiConsole.MarkupLine($"[red]内部错误: {Markup.Escape(ex.InnerException.Message)}[/]");
        }
    }

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
        AnsiConsole.MarkupLine($"已加载 [green]{rules.Fields.Count}[/] 条字段规则。");
        return rules;
    }

    private static bool ResolveFlatten(AnalysisOptions options, AnalysisRules? rules)
    {
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
                    Flatten = p.Flatten
                })
                .ToList();
        }

        var legacyColumns = new List<string>();
        if (!string.IsNullOrWhiteSpace(options.ExcelJsonColumns))
        {
            legacyColumns.AddRange(ParseColumnList(options.ExcelJsonColumns));
        }
        else if (!string.IsNullOrWhiteSpace(options.ExcelJsonColumn))
        {
            legacyColumns.Add(options.ExcelJsonColumn.Trim());
        }

        if (legacyColumns.Count == 0 && rules != null)
        {
            if (rules.ExcelJsonColumns is { Count: > 0 })
            {
                legacyColumns.AddRange(rules.ExcelJsonColumns.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()));
            }
            else if (!string.IsNullOrWhiteSpace(rules.ExcelJsonColumn))
            {
                legacyColumns.Add(rules.ExcelJsonColumn.Trim());
            }
        }

        if (legacyColumns.Count == 0)
        {
            return Array.Empty<ValueParserConfig>();
        }

        return legacyColumns
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(c => new ValueParserConfig { Source = c, ParseType = "json" })
            .ToList();
    }

    private static List<string> ParseColumnList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return new();

        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string>? SplitFields(string? fields)
    {
        if (string.IsNullOrWhiteSpace(fields))
            return null;
        return fields.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
