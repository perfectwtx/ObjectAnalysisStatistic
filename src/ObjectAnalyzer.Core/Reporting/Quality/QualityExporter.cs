using System.Text.Json;
using Spectre.Console;

namespace ObjectAnalyzer.Reporting;

/// <summary>
/// 导出结构化数据质量报告 JSON（Pass/Fail 摘要 + 违规率 + 逐字段明细），
/// 便于 CI 断言或人工快速判断数据质量。
/// </summary>
public static class QualityExporter
{
    public static void Save(AnalysisResult result, string outputPath, bool redactPii = true)
    {
        result = PiiRedactor.Redact(result, redactPii);

        var report = DataQualityReport.Build(result);

        string json = JsonSerializer.Serialize(report, JsonSharedOptions.Write);
        File.WriteAllText(outputPath, json);

        if (report.Passed)
        {
            AnsiConsole.MarkupLine($"[green]数据质量通过: {report.TotalFields} 个字段，无枚举违规。[/]");
        }
        else
        {
            AnsiConsole.MarkupLine(
                $"[yellow]数据质量未通过: {report.FieldsWithViolations}/{report.TotalFields} 个字段存在 {report.TotalViolationCount} 条枚举违规（违规率 {report.ViolationRate:P1}）。[/]");
        }
        AnsiConsole.MarkupLine($"已导出数据质量报告至: [blue]{Markup.Escape(outputPath)}[/]");
    }
}
