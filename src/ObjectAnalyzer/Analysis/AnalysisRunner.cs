using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Spectre.Console;

namespace ObjectAnalyzer;

public static class AnalysisRunner
{
    /// <summary>
    /// Runs the analysis pipeline. Returns 0 on success, 1 on failure.
    /// </summary>
    public static int Run(AnalysisOptions options)
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

            // 公共后处理：分析 → 报告 → 导出
            ProcessAndReport(parsedObjects, options);
            return 0;
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
    private static List<Dictionary<string, object?>> ParseByFormat(AnalysisOptions options)
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

        // 注意：context 必须在枚举完成后才能 Dispose，
        // 但由于 Analyze 内部会 ToList() 物化，这里在枚举后释放是安全的。
        // 我们使用一个包装来确保 context 在适当时机关闭。
        var targetType = compileResult.type;
        AnsiConsole.MarkupLine($"编译成功。目标类型: [green]{targetType.FullName}[/]");

        AnsiConsole.MarkupLine($"正在使用强类型反序列化文件: [blue]{Markup.Escape(options.InputPath)}[/] ...");
        var typedResult = TypedFileParser.ParseJson(options.InputPath, targetType);

        // 物化后立即释放编译上下文，避免内存泄漏
        var materialized = new List<Dictionary<string, object?>>();
        try
        {
            materialized.AddRange(typedResult.Objects);

            // 提示属性读取错误（不阻断分析）
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
    /// Common post-processing: analyze objects, print console report, and optionally export.
    /// </summary>
    private static void ProcessAndReport(IEnumerable<Dictionary<string, object?>> parsedObjects, AnalysisOptions options)
    {
        // 加载规则文件
        AnalysisRules? rules = null;
        if (!string.IsNullOrEmpty(options.RulesFile))
        {
            rules = LoadRules(options.RulesFile);
        }

        AnsiConsole.MarkupLine("正在分析对象字段...");
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = options.MaxDegreeOfParallelism > 0
                ? options.MaxDegreeOfParallelism
                : -1 // -1 = 不限制，由运行时决定（通常为所有核心）
        };
        AnalysisResult result = ObjectAnalyzer.Analyze(parsedObjects, rules, parallelOptions);
        AnsiConsole.MarkupLine("[green]分析完成！[/]\n");

        // 输出控制台统计结果
        ConsoleReporter.Print(result, options.MaxValuesToShow);

        // 如果指定了输出路径，则保存详细报告
        if (!string.IsNullOrEmpty(options.OutputPath))
        {
            ReportExporter.Save(result, options.OutputPath);
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
    /// 从 JSON 文件加载分析规则。
    /// </summary>
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
