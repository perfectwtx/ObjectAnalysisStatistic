using System.Text.Json;
using ObjectAnalyzer.Analysis.Rules;
using Spectre.Console;

namespace ObjectAnalyzer.Cli;

internal static class CliPreflight
{
    /// <summary>
    /// 加载规则文件并运行预检：以 Spectre 表格形式输出对比结果。
    /// 与 Web 端使用同一份 ObjectAnalyzer.Analysis.PreflightChecker 实现。
    /// </summary>
    public static PreflightOutcome Run(
        FileInfo input, FileInfo rulesFile, bool noFlatten, bool allowUnknownRules, bool noCsvInferNumbers)
    {
        // 1) 加载规则（与 AnalysisRunner 走同一路径：RulesLint 先扫未知键，再 JSON 反序列化）
        string rulesText = File.ReadAllText(rulesFile.FullName);
        if (!allowUnknownRules)
        {
            var unknown = RulesLint.Scan(rulesText);
            if (unknown.Count > 0)
            {
                AnsiConsole.MarkupLine("[red]规则文件含有未识别的键：[/]");
                foreach (var key in unknown)
                {
                    AnsiConsole.MarkupLine($"  [red]-[/] {Markup.Escape(key.Path + "." + key.Name)}");
                }
                AnsiConsole.MarkupLine("[grey]提示: --allow-unknown-rules 可绕过此检查（不推荐）。[/]");
                return PreflightOutcome.AbortWithError;
            }
        }
        AnalysisRules? rules;
        try
        {
            rules = JsonSerializer.Deserialize<AnalysisRules>(rulesText, AnalysisRules.RulesJsonOptions);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]规则文件反序列化失败: {Markup.Escape(ex.Message)}[/]");
            return PreflightOutcome.AbortWithError;
        }
        if (rules == null)
        {
            AnsiConsole.MarkupLine("[red]规则文件反序列化结果为空。[/]");
            return PreflightOutcome.AbortWithError;
        }

        // 2) 构造 ParseOptions：复用与正式分析一致的解析参数（valueParsers / 展平 / CSV 推断）
        var parseOptions = new ParseOptions
        {
            Flatten = !noFlatten,
            ValueParsers = rules.ValueParsers ?? new List<ValueParserConfig>(),
            CsvInferNumbers = !noCsvInferNumbers
        };
        var format = FileParsers.DetectFormat(input.FullName);
        // 规则带 valueParsers 时切换为 excel-json
        if (rules.ValueParsers is { Count: > 0 } && format == "excel") format = "excel-json";

        // 3) 跑预检 + 输出
        var result = PreflightChecker.Check(input.FullName, rules, parseOptions, format);
        Render(rules, result, format);

        return result.HasWarning ? PreflightOutcome.WarningFound : PreflightOutcome.NoRulesOrNoWarning;
    }

    // 把 PreflightResult 渲染为 Spectre 表格：左侧规则字段（已用颜色区分匹配/未匹配），右侧样本字段。
    private static void Render(AnalysisRules rules, PreflightResult result, string format)
    {
        AnsiConsole.MarkupLine($"[blue]预检：[/] 数据格式 [yellow]{Markup.Escape(format)}[/]，规则字段 {rules.Fields.Count} 个，样本字段 {result.SampleFields.Count} 个" +
            (result.TruncatedSampleFieldCount > 0 ? $"（已截断 {result.TruncatedSampleFieldCount} 个未展示）" : ""));

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("[bold]规则字段[/]");
        table.AddColumn(new TableColumn("[bold]状态[/]").Centered());
        table.AddColumn("[bold]样本字段（前 20）[/]");

        string sampleList = result.SampleFields.Count > 0
            ? string.Join("\n", result.SampleFields.Select(Markup.Escape))
            : "[grey]（无样本字段）[/]";
        if (result.TruncatedSampleFieldCount > 0)
        {
            sampleList += $"\n[grey]…还有 {result.TruncatedSampleFieldCount} 个未展示[/]";
        }

        // 单行展示：左侧列出全部规则字段 + 状态，右侧列出样本字段
        var ruleFieldMarkup = result.RuleFields.Count > 0
            ? string.Join("\n", result.RuleFields.Select(Markup.Escape))
            : "[grey]（无规则字段）[/]";
        var statusMarkup = result.HasWarning
            ? "[red]无匹配[/]"
            : result.MatchedRuleFields.Count == 0
                ? "[grey]无规则[/]"
                : $"[green]已匹配 {result.MatchedRuleFields.Count}/{result.RuleFields.Count}[/]";

        table.AddRow(ruleFieldMarkup, statusMarkup, sampleList);
        AnsiConsole.Write(table);

        if (result.HasWarning)
        {
            AnsiConsole.MarkupLine("[red]规则与数据无任何字段匹配，建议检查规则文件或换用对应数据。[/]");
        }
    }
}

/// <summary>
/// 预检结果分类：用于 CLI 决定是直接放行、警告后询问，还是直接退出。
/// </summary>
internal enum PreflightOutcome
{
    NoRulesOrNoWarning,  // 没有规则文件 / 预检无警告
    WarningFound,        // 有警告（规则与数据不匹配）
    AbortWithError       // 加载规则失败（语法错误 / 未知键）
}
