using System;
using System.CommandLine;
using System.IO;
using MiniExcelLibs;
using ObjectAnalyzer;

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

var rootCommand = new RootCommand("对象字段分析统计工具 (Object Analyzer)");
rootCommand.AddOption(inputOption);
rootCommand.AddOption(outputOption);
rootCommand.AddOption(formatOption);
rootCommand.AddOption(noFlattenOption);
rootCommand.AddOption(maxValuesOption);
rootCommand.AddOption(generateTestExcelOption);

rootCommand.SetHandler((input, output, format, noFlatten, maxValues, generateTestExcel) =>
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

    AnalysisRunner.Run(input.FullName, output?.FullName, format, !noFlatten, maxValues);
}, inputOption, outputOption, formatOption, noFlattenOption, maxValuesOption, generateTestExcelOption);

return rootCommand.Invoke(args);

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
        MiniExcel.SaveAs("samples/test_data.xlsx", data);
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
