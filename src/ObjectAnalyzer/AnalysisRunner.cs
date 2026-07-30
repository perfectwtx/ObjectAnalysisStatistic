using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ObjectAnalyzer;

public static class AnalysisRunner
{
    public static void Run(string inputPath, string? outputPath, string? format, bool flatten, int maxValuesToShow, string? typeFile = null, string? typeName = null)
    {
        if (!File.Exists(inputPath))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"错误: 输入文件不存在: {inputPath}");
            Console.ResetColor();
            return;
        }

        // 如果指定了 --type，使用强类型反序列化路径
        if (!string.IsNullOrEmpty(typeFile))
        {
            RunTyped(inputPath, outputPath, flatten, maxValuesToShow, typeFile, typeName);
            return;
        }

        // 自动检测文件格式
        if (string.IsNullOrEmpty(format))
        {
            string ext = Path.GetExtension(inputPath).ToLowerInvariant();
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

        try
        {
            Console.WriteLine($"正在读取并解析文件: {inputPath} ...");
            IEnumerable<Dictionary<string, object?>> parsedObjects = format switch
            {
                "json" => FileParsers.ParseJson(inputPath, flatten),
                "jsonl" => FileParsers.ParseJsonl(inputPath, flatten),
                "csv" or "excel" => FileParsers.ParseExcelOrCsv(inputPath),
                "xml" => FileParsers.ParseXml(inputPath),
                "yaml" => FileParsers.ParseYaml(inputPath),
                _ => throw new InvalidOperationException($"不支持的文件格式: {format}")
            };

            var objectList = parsedObjects.ToList();
            Console.WriteLine($"解析成功。共加载 {objectList.Count} 个对象。");

            if (objectList.Count == 0)
            {
                Console.WriteLine("未找到任何有效对象。分析结束。");
                return;
            }

            Console.WriteLine("正在分析对象字段...");
            AnalysisResult result = ObjectAnalyzer.Analyze(objectList);
            Console.WriteLine("分析完成！\n");

            // 输出控制台统计结果
            ConsoleReporter.Print(result, maxValuesToShow);

            // 如果指定了输出路径，则保存详细报告
            if (!string.IsNullOrEmpty(outputPath))
            {
                ReportExporter.Save(result, outputPath);
            }
        }
        catch (Exception ex)
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

    private static void RunTyped(string inputPath, string? outputPath, bool flatten, int maxValuesToShow, string typeFile, string? typeName)
    {
        try
        {
            Console.WriteLine($"正在编译类型定义文件: {typeFile} ...");
            var (assembly, targetType) = TypeLoader.Compile(typeFile, typeName);
            Console.WriteLine($"编译成功。目标类型: {targetType.FullName}");

            Console.WriteLine($"正在使用强类型反序列化文件: {inputPath} ...");
            var objectList = TypedFileParser.ParseJson(inputPath, targetType).ToList();
            Console.WriteLine($"反序列化成功。共加载 {objectList.Count} 个对象。");

            if (objectList.Count == 0)
            {
                Console.WriteLine("未找到任何有效对象。分析结束。");
                return;
            }

            Console.WriteLine("正在分析对象字段...");
            AnalysisResult result = ObjectAnalyzer.Analyze(objectList);
            Console.WriteLine("分析完成！\n");

            ConsoleReporter.Print(result, maxValuesToShow);

            if (!string.IsNullOrEmpty(outputPath))
            {
                ReportExporter.Save(result, outputPath);
            }
        }
        catch (Exception ex)
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
}
