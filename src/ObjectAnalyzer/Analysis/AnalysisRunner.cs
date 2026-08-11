using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using Spectre.Console;

namespace ObjectAnalyzer;

public static class AnalysisRunner
{
    /// <summary>
    /// Runs the analysis pipeline. Returns 0 on success, 1 on failure, 2 on cancellation.
    /// </summary>
    public static int Run(AnalysisOptions options, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(options.InputPath))
        {
            AnsiConsole.MarkupLine($"[red]错误: 输入文件不存在: {Markup.Escape(options.InputPath)}[/]");
            return 1;
        }

        try
        {
            IEnumerable<Dictionary<string, object?>> parsedObjects;

            // 如果指定了 --type，使用强类型反序列化路径
            if (!string.IsNullOrEmpty(options.TypeFile))
            {
                parsedObjects = RunTyped(options);
            }
            else
            {
                parsedObjects = ParseByFormat(options);
            }

            // 公共后处理：分析 → 报告 → 导出 → Schema 代码导出
            AnalysisResult result = ProcessAndReport(parsedObjects, options, cancellationToken);

            // CI 数据质量门禁：存在枚举违规时以非零退出码结束
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
    private static IEnumerable<Dictionary<string, object?>> ParseByFormat(AnalysisOptions options)
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
            Flatten = options.Flatten,
            ExcelJsonColumn = options.ExcelJsonColumn ?? "A"
        };
        return FileParsers.ParseByFormat(options.InputPath, format, parseOptions);
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

        return materialized;
    }

    /// <summary>
    /// Common post-processing: analyze objects, print console report, export reports and optional schema.
    /// </summary>
    private static AnalysisResult ProcessAndReport(
        IEnumerable<Dictionary<string, object?>> parsedObjects,
        AnalysisOptions options,
        CancellationToken cancellationToken = default)
    {
        // 加载规则文件
        AnalysisRules? rules = null;
        if (!string.IsNullOrEmpty(options.RulesFile))
        {
            rules = LoadRules(options.RulesFile);
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

        AnsiConsole.MarkupLine("[green]分析完成！[/]\n");

        // 输出控制台统计结果
        ConsoleReporter.Print(result, options.MaxValuesToShow);

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

        // 如果指定了 Schema 导出，自动生成 C# / TypeScript / JSON Schema 定义
        if (!string.IsNullOrEmpty(options.ExportSchema))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string schemaPath = options.SchemaOutputPath ?? Path.ChangeExtension(
                options.OutputPath ?? options.InputPath,
                options.ExportSchema.ToLowerInvariant() switch
                {
                    "csharp" or "cs" => ".cs",
                    "typescript" or "ts" => ".d.ts",
                    _ => ".schema.json"
                });

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
}
