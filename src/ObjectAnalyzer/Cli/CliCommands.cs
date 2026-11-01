using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using System.Text.Json.Serialization;
using ObjectAnalyzer.Analysis.Rules;
using ObjectAnalyzer.Security;
using Spectre.Console;

namespace ObjectAnalyzer.Cli;

internal static class CliCommands
{
    private static readonly JsonSerializerOptions s_rulesJsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// 离线 KMS：用环境变量口令对明文密钥加密，输出可直接写入规则文件 transform 的 kms:offline/... 引用。
    /// 明文从 stdin 读取（不落命令行参数 / shell 历史），口令从 OA_KMS_PASSPHRASE 环境变量读取。
    /// </summary>
    public static Command CreateKmsSecret()
    {
        var encodingOption = new Option<string?>(
            "--encoding",
            description: "明文密钥的编码还原方式：utf8（默认）、base64 或 hex。base64/hex 表示把解密出的字节重新编码为该形式，供 transform 的 format 字段使用。",
            getDefaultValue: () => "utf8");
        var plainOption = new Option<string?>(
            "--value",
            description: "要加密的明文密钥（可选）。不指定则从 stdin 读取——推荐，避免明文出现在 shell 历史。");

        var command = new Command("kms-secret", "离线 KMS：用环境变量 OA_KMS_PASSPHRASE 的口令加密明文密钥，生成规则文件可直接引用的 kms:offline/... 密文。");
        command.AddOption(encodingOption);
        command.AddOption(plainOption);
        command.SetHandler((InvocationContext context) =>
        {
            try
            {
                var parseResult = context.ParseResult;
                string? plainValue = parseResult.GetValueForOption(plainOption);
                string? encoding = parseResult.GetValueForOption(encodingOption);
                string plaintext = plainValue ?? Console.In.ReadToEnd().TrimEnd('\r', '\n', ' ');
                if (string.IsNullOrEmpty(plaintext))
                {
                    AnsiConsole.MarkupLine("[red]错误: 明文密钥为空。请通过 --value 或 stdin 提供要加密的密钥。[/]");
                    context.ExitCode = 1;
                    return;
                }

                string kmsRef = OfflineKmsProvider.Encrypt(plaintext, encoding ?? "utf8");
                AnsiConsole.MarkupLine("[green]加密成功。将以下值粘贴到规则文件 transform 的 key/iv/privateKey 等字段即可：[/]");
                Console.WriteLine(kmsRef);
                context.ExitCode = 0;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]错误: {Markup.Escape(ex.Message)}[/]");
                context.ExitCode = 1;
            }
        });
        return command;
    }

    /// <summary>
    /// 从 C# 源码静态提取类结构，生成规则文件骨架（primaryType/枚举/必填/范围/模式/默认值）。
    /// 仅静态解析，不编译、不参与分析管线；数据统计类规则（唯一/null 率/离群率）需后续手工补充。
    /// </summary>
    public static Command CreateRulesFromCode()
    {
        var (inputOption, outputOption, classOption) = CreateRulesOptions();
        var command = new Command("rules-from-code", "解析 C# 源码中的类/记录/枚举声明，生成规则文件骨架。");
        command.AddOption(inputOption);
        command.AddOption(classOption);
        command.AddOption(outputOption);
        command.SetHandler((InvocationContext context) =>
        {
            try
            {
                var parseResult = context.ParseResult;
                var input = parseResult.GetValueForOption(inputOption)!;
                var className = parseResult.GetValueForOption(classOption);
                var output = parseResult.GetValueForOption(outputOption)!;

                var warnings = new List<string>();
                var rules = RulesFromCSharp.Extract(File.ReadAllText(input.FullName), className, warnings);
                foreach (var warning in warnings)
                {
                    AnsiConsole.MarkupLine($"[yellow]跳过: {Markup.Escape(warning)}[/]");
                }
                WriteRulesFile(rules, output, context, $"C# {className ?? input.Name}");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]错误: {Markup.Escape(ex.Message)}[/]");
                context.ExitCode = 1;
            }
        });
        return command;
    }

    /// <summary>把 JSON Schema（draft-07 / 2020-12 常用子集）转换为规则文件骨架。</summary>
    public static Command CreateRulesFromSchema()
    {
        var (inputOption, outputOption, _) = CreateRulesOptions();
        var command = new Command("rules-from-schema", "把 JSON Schema（draft-07 / 2020-12 常用子集）转换为规则文件骨架。");
        command.AddOption(inputOption);
        command.AddOption(outputOption);
        command.SetHandler((InvocationContext context) =>
        {
            try
            {
                var parseResult = context.ParseResult;
                var input = parseResult.GetValueForOption(inputOption)!;
                var output = parseResult.GetValueForOption(outputOption)!;

                using var doc = JsonDocument.Parse(File.ReadAllText(input.FullName));
                var skipped = new List<string>();
                var rules = RulesFromSchema.Convert(doc.RootElement, skipped);
                foreach (var skippedPath in skipped)
                {
                    AnsiConsole.MarkupLine($"[yellow]跳过: {Markup.Escape(skippedPath)}[/]");
                }
                WriteRulesFile(rules, output, context, $"JSON Schema {input.Name}");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]错误: {Markup.Escape(ex.Message)}[/]");
                context.ExitCode = 1;
            }
        });
        return command;
    }

    private static (Option<FileInfo> Input, Option<FileInfo> Output, Option<string?> Class) CreateRulesOptions()
    {
        var inputOption = new Option<FileInfo>(
            aliases: CliAliases.Input,
            description: "输入文件路径（rules-from-code: .cs 源码；rules-from-schema: JSON Schema 文件）。")
        { IsRequired = true };
        var classOption = new Option<string?>(
            aliases: CliAliases.Class,
            description: "目标类名（仅 rules-from-code）；源码包含多个类时必填。");
        var outputOption = new Option<FileInfo>(
            aliases: CliAliases.Output,
            description: "生成的规则文件输出路径（.json）。")
        { IsRequired = true };
        return (inputOption, outputOption, classOption);
    }

    private static void WriteRulesFile(AnalysisRules rules, FileInfo output, InvocationContext context, string sourceDesc)
    {
        var json = JsonSerializer.Serialize(rules, s_rulesJsonOptions);
        File.WriteAllText(output.FullName, json);
        AnsiConsole.MarkupLine(
            $"[green]已生成规则文件[/] [blue]{Markup.Escape(output.FullName)}[/]（来源: {Markup.Escape(sourceDesc)}，{rules.Fields.Count} 个字段规则）。");
        AnsiConsole.MarkupLine("[grey]提示: 生成的是结构约束骨架（类型/枚举/必填/范围/模式/默认值）；唯一率、null 率、离群率等数据统计类规则需按实际数据手工补充。[/]");
        context.ExitCode = 0;
    }
}
