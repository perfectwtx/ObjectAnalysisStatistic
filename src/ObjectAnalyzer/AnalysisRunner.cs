using System;
using System.Collections.Generic;
using System.IO;

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
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"错误: 输入文件不存在: {options.InputPath}");
            Console.ResetColor();
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
    /// </summary>
    private static IEnumerable<Dictionary<string, object?>> ParseByFormat(AnalysisOptions options)
    {
        string? format = options.Format;

        // 自动检测文件格式
        if (string.IsNullOrEmpty(format))
        {
            string ext = Path.GetExtension(options.InputPath).ToLowerInvariant();
            format = ext switch
            {
                ".json" => "json",
                ".jsonl" or ".ndjson" => "jsonl",
                ".csv" => "csv",
                ".xlsx" => "excel",
                ".xml" => "xml",
                ".yaml" or ".yml" => "yaml",
                ".txt" => "jsonl", // 默认文本文件尝试以 JSONL 解析
                _ => "json" // 默认退回到 json
            };
            Console.WriteLine($"[自动检测] 输入文件扩展名为 '{ext}'，将使用格式 '{format}' 进行解析。");
        }

        Console.WriteLine($"正在读取并解析文件: {options.InputPath} ...");
        return format switch
        {
            "json" => FileParsers.ParseJson(options.InputPath, options.Flatten),
            "jsonl" => FileParsers.ParseJsonl(options.InputPath, options.Flatten),
            "csv" or "excel" => FileParsers.ParseExcelOrCsv(options.InputPath),
            "xml" => FileParsers.ParseXml(options.InputPath),
            "yaml" => FileParsers.ParseYaml(options.InputPath),
            "excel-json" => FileParsers.ParseExcelJsonColumn(options.InputPath, options.ExcelJsonColumn ?? "A", options.Flatten),
            _ => throw new InvalidOperationException($"不支持的文件格式: {format}")
        };
    }

    /// <summary>
    /// Compiles the type definition file and performs strongly-typed deserialization.
    /// </summary>
    private static List<Dictionary<string, object?>> RunTyped(AnalysisOptions options)
    {
        Console.WriteLine($"正在编译类型定义文件: {options.TypeFile} ...");
        var compileResult = TypeLoader.Compile(options.TypeFile!, options.TypeName);

        // 注意：context 必须在枚举完成后才能 Dispose，
        // 但由于 Analyze 内部会 ToList() 物化，这里在枚举后释放是安全的。
        // 我们使用一个包装来确保 context 在适当时机关闭。
        var targetType = compileResult.type;
        Console.WriteLine($"编译成功。目标类型: {targetType.FullName}");

        Console.WriteLine($"正在使用强类型反序列化文件: {options.InputPath} ...");
        var objects = TypedFileParser.ParseJson(options.InputPath, targetType);

        // 物化后立即释放编译上下文，避免内存泄漏
        var materialized = new List<Dictionary<string, object?>>();
        try
        {
            foreach (var obj in objects)
                materialized.Add(obj);
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
        Console.WriteLine("正在分析对象字段...");
        AnalysisResult result = ObjectAnalyzer.Analyze(parsedObjects);
        Console.WriteLine("分析完成！\n");

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
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"发生错误: {ex.Message}");
        if (ex.InnerException != null)
        {
            Console.WriteLine($"内部错误: {ex.InnerException.Message}");
        }
        Console.ResetColor();
    }
}
