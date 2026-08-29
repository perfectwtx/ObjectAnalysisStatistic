using System.IO;
using System.Text.Json;
using Spectre.Console;

namespace ObjectAnalyzer.Reporting;

/// <summary>
/// 导出数据安全合规等级报告 JSON（合规等级 + 敏感字段清单 + 各类别统计），
/// 便于 CI 或人工在共享报告前快速判定合规风险。
/// </summary>
public static class PiiExporter
{
    public static void Save(AnalysisResult result, string outputPath)
    {
        var report = PiiReport.Build(result);

        string json = JsonSerializer.Serialize(report, JsonSharedOptions.Write);
        File.WriteAllText(outputPath, json);

        AnsiConsole.MarkupLine(
            report.PiiFieldCount == 0
                ? $"[green]数据安全合规等级: A（未发现敏感字段）。[/]"
                : $"[yellow]数据安全合规等级: [bold]{report.ComplianceLevel}[/]"
                  + $"（{Markup.Escape(report.ComplianceLevelLabel)}）：{report.PiiFieldCount} 个敏感字段，"
                  + $"共 {report.PiiValueCount} 个敏感值，全部自动打码。[/]");
        AnsiConsole.MarkupLine($"已导出数据安全合规报告至: [blue]{Markup.Escape(outputPath)}[/]");
    }
}