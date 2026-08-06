using System;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.IO;
using ObjectAnalyzer;
using Spectre.Console;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var inputOption = new Option<FileInfo?>(
    aliases: CliAliases.Input,
    description: "指定输入的 JSON、JSONL、CSV、Excel、XML、YAML 文件路径。");

var outputOption = new Option<FileInfo?>(
    aliases: CliAliases.Output,
    description: "指定保存分析报告的目标文件路径 (.json / .html / .md / .csv)。");

var formatOption = new Option<string?>(
    aliases: CliAliases.Format,
    description: "显式指定输入文件格式: json, jsonl, csv, excel, xml, yaml, excel-json。");
formatOption.AddCompletions("json", "jsonl", "csv", "excel", "xml", "yaml", "excel-json");

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

var excelJsonColumnOption = new Option<string?>(
    aliases: CliAliases.ExcelJsonColumn,
    description: "指定 Excel 文件中包含 JSON 字符串的列名（如 A、B、C）。与 --format excel-json 配合使用。");
excelJsonColumnOption.AddCompletions("A", "B", "C", "D", "E");

var rulesOption = new Option<FileInfo?>(
    aliases: CliAliases.Rules,
    description: "指定字段规则配置文件路径 (.json)，用于定义默认值、主导类型和枚举约束。");

var parallelOption = new Option<int>(
    aliases: CliAliases.Parallel,
    description: "指定分析时的最大并行线程数 (默认使用所有核心)。",
    getDefaultValue: () => -1);

var violationsOption = new Option<FileInfo?>(
    aliases: CliAliases.Violations,
    description: "将枚举违规导出为独立的结构化 JSON 文件路径。");

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
rootCommand.AddOption(rulesOption);
rootCommand.AddOption(parallelOption);
rootCommand.AddOption(violationsOption);

rootCommand.SetHandler((InvocationContext context) =>
{
    var parseResult = context.ParseResult;
    var input = parseResult.GetValueForOption(inputOption);
    var output = parseResult.GetValueForOption(outputOption);
    var format = parseResult.GetValueForOption(formatOption);
    var noFlatten = parseResult.GetValueForOption(noFlattenOption);
    var maxValues = parseResult.GetValueForOption(maxValuesOption);
    var generateTestExcel = parseResult.GetValueForOption(generateTestExcelOption);
    var type = parseResult.GetValueForOption(typeOption);
    var typeName = parseResult.GetValueForOption(typeNameOption);
    var excelJsonColumn = parseResult.GetValueForOption(excelJsonColumnOption);
    var rules = parseResult.GetValueForOption(rulesOption);
    var maxParallel = parseResult.GetValueForOption(parallelOption);
    var violations = parseResult.GetValueForOption(violationsOption);

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

    if (input == null)
    {
        AnsiConsole.MarkupLine("[red]错误: 未指定输入文件。使用 -i 或 --input 指定。[/]");
        AnsiConsole.MarkupLine("[grey]提示: 使用 -h 或 --help 查看使用说明。[/]");
        context.ExitCode = 1;
        return;
    }

    var options = new AnalysisOptions
    {
        InputPath = input.FullName,
        OutputPath = output?.FullName,
        Format = format,
        Flatten = !noFlatten,
        MaxValuesToShow = maxValues,
        TypeFile = type?.FullName,
        TypeName = typeName,
        ExcelJsonColumn = excelJsonColumn ?? "A",
        RulesFile = rules?.FullName,
        MaxDegreeOfParallelism = maxParallel,
        ViolationsPath = violations?.FullName
    };

    context.ExitCode = AnalysisRunner.Run(options);
});

return rootCommand.Invoke(args);

static class CliAliases
{
    public static readonly string[] Input = { "--input", "-i" };
    public static readonly string[] Output = { "--output", "-o" };
    public static readonly string[] Format = { "--format", "-f" };
    public static readonly string[] MaxValues = { "--max-values", "-m" };
    public static readonly string[] Type = { "--type", "-t" };
    public static readonly string[] ExcelJsonColumn = { "--excel-json-column", "-c" };
    public static readonly string[] Rules = { "--rules", "-r" };
    public static readonly string[] Parallel = { "--parallel", "-p" };
    public static readonly string[] Violations = { "--violations" };
}
