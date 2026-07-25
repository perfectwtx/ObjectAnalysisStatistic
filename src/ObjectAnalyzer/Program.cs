using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ObjectAnalyzer;

class Program
{
    static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var inputOption = new Option<FileInfo?>(
            aliases: new[] { "--input", "-i" },
            description: "指定输入的 JSON、JSONL、CSV 或 Excel 文件路径。");

        var outputOption = new Option<FileInfo?>(
            aliases: new[] { "--output", "-o" },
            description: "指定保存详细分析报告的目标文件路径 (.json)。");

        var formatOption = new Option<string?>(
            aliases: new[] { "--format", "-f" },
            description: "显式指定输入文件格式: json, jsonl, csv, excel。");

        var noFlattenOption = new Option<bool>(
            name: "--no-flatten",
            description: "解析 JSON 时，不扁平化嵌套的属性路径。");

        var maxValuesOption = new Option<int>(
            aliases: new[] { "--max-values", "-m" },
            description: "控制台报告中每个字段最多展示的唯一值数量。",
            getDefaultValue: () => 5);

        var generateTestExcelOption = new Option<bool>(
            name: "--generate-test-excel",
            description: "生成一个 Excel 测试文件 samples/test_data.xlsx 并退出。");

        var rootCommand = new RootCommand("对象字段分析统计工具 (Object Analysis Statistic Tool)");
        rootCommand.AddOption(inputOption);
        rootCommand.AddOption(outputOption);
        rootCommand.AddOption(formatOption);
        rootCommand.AddOption(noFlattenOption);
        rootCommand.AddOption(maxValuesOption);
        rootCommand.AddOption(generateTestExcelOption);

        rootCommand.SetHandler((FileInfo? input, FileInfo? output, string? format, bool noFlatten, int maxValues, bool generateTestExcel) =>
        {
            if (generateTestExcel)
            {
                GenerateTestExcel();
                return;
            }

            if (input == null)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("错误: 未指定输入文件。使用 -i 或 --input 指定。");
                Console.ResetColor();
                Console.WriteLine("提示: 使用 -h 或 --help 查看使用说明。");
                return;
            }

            RunAnalysis(input.FullName, output?.FullName, format, !noFlatten, maxValues);
        }, inputOption, outputOption, formatOption, noFlattenOption, maxValuesOption, generateTestExcelOption);

        return rootCommand.Invoke(args);
    }

    static void RunAnalysis(string inputPath, string? outputPath, string? format, bool flatten, int maxValuesToShow)
    {
        if (!File.Exists(inputPath))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"错误: 输入文件不存在: {inputPath}");
            Console.ResetColor();
            return;
        }

        // 自动检测文件格式
        if (string.IsNullOrEmpty(format))
        {
            string ext = Path.GetExtension(inputPath).ToLower();
            format = ext switch
            {
                ".json" => "json",
                ".jsonl" or ".ndjson" => "jsonl",
                ".csv" => "csv",
                ".xlsx" => "excel",
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
            PrintConsoleReport(result, maxValuesToShow);

            // 如果指定了输出路径，则保存详细报告
            if (!string.IsNullOrEmpty(outputPath))
            {
                SaveReport(result, outputPath);
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

    static void PrintConsoleReport(AnalysisResult result, int maxValuesToShow)
    {
        Console.WriteLine("================================================================================");
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"                      分析摘要报告 (总对象数: {result.TotalObjects})");
        Console.ResetColor();
        Console.WriteLine("================================================================================");

        // 如果存在重复对象，打印重复对象统计
        if (result.DuplicateObjects.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[重复对象统计] 发现 {result.DuplicateObjects.Count} 组重复出现的对象 (不同对象数: {result.TotalUniqueObjects})");
            Console.ResetColor();
            foreach (var dup in result.DuplicateObjects.Take(3)) // 控制台最多显示 3 组
            {
                Console.WriteLine($"  • 重复出现 {dup.Count} 次: {dup.ObjectJson}");
            }
            if (result.DuplicateObjects.Count > 3)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"  ... 还有 {result.DuplicateObjects.Count - 3} 组重复对象未列出");
                Console.ResetColor();
            }
            Console.WriteLine("--------------------------------------------------------------------------------");
        }

        foreach (var stat in result.FieldStatistics)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write($"字段: ");
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write($"{stat.FieldName,-35}");
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write($" 频次: ");
            Console.ResetColor();
            Console.Write($"{stat.Count}/{result.TotalObjects} ({stat.Coverage * 100:F1}%)");
            Console.WriteLine();

            // 打印类型统计与推断的默认值说明
            string defaultValInfo = stat.DefaultValueRepresentation != null ? $" (推断默认值: {stat.DefaultValueRepresentation})" : "";
            string typesStr = string.Join(", ", stat.TypeCounts.Select(t => $"{t.Key}({t.Value})"));
            Console.WriteLine($"  ├─ 数据类型: {typesStr}{defaultValInfo}");

            // 打印值频率
            Console.WriteLine($"  └─ 唯一值频次 (最多展示前 {maxValuesToShow} 个):");
            var sortedValues = stat.ValueCounts.OrderByDescending(v => v.Value).ToList();
            int shownCount = 0;
            foreach (var val in sortedValues)
            {
                if (shownCount >= maxValuesToShow)
                {
                    int remaining = sortedValues.Count - maxValuesToShow;
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine($"     ... 还有 {remaining} 个唯一值已省略");
                    Console.ResetColor();
                    break;
                }

                string displayVal = val.Key;
                if (displayVal.Length > 50)
                {
                    displayVal = displayVal.Substring(0, 47) + "...";
                }
                displayVal = displayVal.Replace("\r", "\\r").Replace("\n", "\\n");

                // 判断是否是该字段推断出的默认值
                bool isDefaultValue = false;
                if (stat.DefaultValueRepresentation != null)
                {
                    if (stat.DefaultValueRepresentation == "null")
                    {
                        isDefaultValue = (val.Key == "null");
                    }
                    else if (stat.DefaultValueRepresentation == "0")
                    {
                        isDefaultValue = (val.Key == "0" || val.Key == "0.0");
                    }
                    else if (stat.DefaultValueRepresentation == "false")
                    {
                        isDefaultValue = string.Equals(val.Key, "false", StringComparison.OrdinalIgnoreCase);
                    }
                }

                string defaultTag = isDefaultValue ? " [默认值]" : "";

                double valPercentage = stat.Count > 0 ? (double)val.Value / stat.Count : 0;
                Console.Write($"     • [{(valPercentage * 100):F1}%] \"{displayVal}\" -> 出现 {val.Value} 次");
                if (isDefaultValue)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.Write(defaultTag);
                    Console.ResetColor();
                }
                Console.WriteLine();
                shownCount++;
            }
            Console.WriteLine();
        }
        Console.WriteLine("================================================================================");
    }

    static void SaveReport(AnalysisResult result, string outputPath)
    {
        try
        {
            Console.WriteLine($"正在将详细分析报告保存至: {outputPath} ...");
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            string jsonString = JsonSerializer.Serialize(result, options);
            File.WriteAllText(outputPath, jsonString);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"报告成功保存！");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"保存报告时出错: {ex.Message}");
            Console.ResetColor();
        }
    }

    static void GenerateTestExcel()
    {
        try
        {
            var data = new[] {
                new Dictionary<string, object?> { { "Id", 1 }, { "Name", "张三" }, { "Age", 28 }, { "Role", "Admin" } },
                new Dictionary<string, object?> { { "Id", 2 }, { "Name", "李四" }, { "Age", null }, { "Role", "User" } },
                new Dictionary<string, object?> { { "Id", 3 }, { "Name", "王五" }, { "Age", 25 }, { "Role", null } }
            };
            Directory.CreateDirectory("samples");
            MiniExcelLibs.MiniExcel.SaveAs("samples/test_data.xlsx", data);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("成功生成 Excel 测试文件: samples/test_data.xlsx");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"生成 Excel 测试文件失败: {ex.Message}");
            Console.ResetColor();
        }
    }
}
