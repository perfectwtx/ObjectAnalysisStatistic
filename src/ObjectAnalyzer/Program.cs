using System;
using System.CommandLine;
using System.IO;
using MiniExcelLibs;
using ObjectAnalyzer;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var inputOption = new Option<FileInfo?>(
    aliases: CliAliases.Input,
    description: "指定输入的 JSON、JSONL、CSV、Excel、XML 或 YAML 文件路径。");

var outputOption = new Option<FileInfo?>(
    aliases: CliAliases.Output,
    description: "指定保存详细分析报告的目标文件路径 (.json)。");

var formatOption = new Option<string?>(
    aliases: CliAliases.Format,
    description: "显式指定输入文件格式: json, jsonl, csv, excel, xml, yaml, excel-json。");

var noFlattenOption = new Option<bool>(
    name: "--no-flatten",
    description: "解析 JSON 时，不扁平化嵌套的属性路径。");

var maxValuesOption = new Option<int>(
    aliases: CliAliases.MaxValues,
    description: "控制台报告中每个字段最多展示的唯一值数量。",
    getDefaultValue: () => 5);

var generateTestExcelOption = new Option<bool>(
    name: "--generate-test-excel",
    description: "生成 Excel 测试文件 (samples/test_data.xlsx 和 samples/test_data_excel_json.xlsx) 并退出。");

var typeOption = new Option<FileInfo?>(
    aliases: CliAliases.Type,
    description: "指定 .cs 类型定义文件路径，用于强类型反序列化。");

var typeNameOption = new Option<string?>(
    name: "--type-name",
    description: "指定目标类名（当 .cs 文件包含多个类时使用）。");

var excelJsonColumnOption = new Option<string>(
    aliases: CliAliases.ExcelJsonColumn,
    description: "指定 Excel 文件中包含 JSON 字符串的列名（如 A、B、C）。与 --format excel-json 配合使用。",
    getDefaultValue: () => "A");

var rootCommand = new RootCommand("对象字段分析统计工具 (Object Analyzer)");
rootCommand.AddOption(inputOption);
rootCommand.AddOption(outputOption);
rootCommand.AddOption(formatOption);
rootCommand.AddOption(noFlattenOption);
rootCommand.AddOption(maxValuesOption);
rootCommand.AddOption(generateTestExcelOption);
rootCommand.AddOption(typeOption);
rootCommand.AddOption(typeNameOption);
rootCommand.AddOption(excelJsonColumnOption);

rootCommand.SetHandler((input, output, format, noFlatten, maxValues, generateTestExcel, type, typeName) =>
{
    if (generateTestExcel)
    {
        GenerateTestExcel();
        GenerateTestExcelJson();
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

    // 手动获取 excelJsonColumn 值（避免 SetHandler 参数数量限制）
    var excelJsonColumn = excelJsonColumnOption.Parse(args).GetValueForOption(excelJsonColumnOption) ?? "A";

    AnalysisRunner.Run(input.FullName, output?.FullName, format, !noFlatten, maxValues, type?.FullName, typeName, excelJsonColumn);
}, inputOption, outputOption, formatOption, noFlattenOption, maxValuesOption, generateTestExcelOption, typeOption, typeNameOption);

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

static void GenerateTestExcelJson()
{
    try
    {
        // A 列每行存储一个 JSON 对象字符串
        var data = new[] {
            new Dictionary<string, object?> { { "A", "{\"id\":1,\"name\":\"张三\",\"age\":28,\"gender\":\"Male\",\"city\":\"北京\"}" } },
            new Dictionary<string, object?> { { "A", "{\"id\":2,\"name\":\"李四\",\"age\":32,\"gender\":\"Male\",\"city\":\"上海\"}" } },
            new Dictionary<string, object?> { { "A", "{\"id\":3,\"name\":\"王五\",\"age\":25,\"gender\":\"Female\",\"city\":\"北京\"}" } }
        };
        Directory.CreateDirectory("samples");
        MiniExcel.SaveAs("samples/test_data_excel_json.xlsx", data);
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("成功生成 Excel JSON 测试文件: samples/test_data_excel_json.xlsx");
        Console.ResetColor();
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"生成 Excel JSON 测试文件失败: {ex.Message}");
        Console.ResetColor();
    }
}

static class CliAliases
{
    public static readonly string[] Input = { "--input", "-i" };
    public static readonly string[] Output = { "--output", "-o" };
    public static readonly string[] Format = { "--format", "-f" };
    public static readonly string[] MaxValues = { "--max-values", "-m" };
    public static readonly string[] Type = { "--type", "-t" };
    public static readonly string[] ExcelJsonColumn = { "--excel-json-column", "-c" };
}
