using System.IO;
using System.Text.Json;
using Spectre.Console;

namespace ObjectAnalyzer;

public static class ReportExporter
{
    public static void Save(AnalysisResult result, string outputPath)
    {
        string ext = Path.GetExtension(outputPath).ToLowerInvariant();
        if (ext is ".html" or ".htm")
        {
            HtmlReportExporter.Save(result, outputPath);
        }
        else
        {
            SaveJson(result, outputPath);
        }
    }

    private static void SaveJson(AnalysisResult result, string outputPath)
    {
        AnsiConsole.Markup($"正在将详细分析报告保存至: [blue]{Markup.Escape(outputPath)}[/] ...");
        AnsiConsole.WriteLine();
        string jsonString = JsonSerializer.Serialize(result, JsonSharedOptions.Write);
        File.WriteAllText(outputPath, jsonString);
        AnsiConsole.MarkupLine($"[green]报告成功保存！[/]");
    }
}
