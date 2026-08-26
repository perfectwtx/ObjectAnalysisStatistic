using System.IO;
using System.Text.Json;
using Spectre.Console;

namespace ObjectAnalyzer.Reporting;

public static class ReportExporter
{
    public static void Save(AnalysisResult result, string outputPath, bool redactPii = true)
    {
        // 敏感字段值统一打码后再输出，保证任何报告格式都不会泄露原始 PII（--pii-off 可显式关闭）
        result = PiiRedactor.Redact(result, redactPii);

        string ext = Path.GetExtension(outputPath).ToLowerInvariant();
        if (ext is ".html" or ".htm")
        {
            HtmlReportExporter.Save(result, outputPath);
        }
        else if (ext is ".md" or ".markdown")
        {
            MarkdownReportExporter.Save(result, outputPath);
        }
        else if (ext is ".csv")
        {
            CsvReportExporter.Save(result, outputPath);
        }
        else if (ext is ".xlsx")
        {
            ExcelReportExporter.Save(result, outputPath);
        }
        else if (ext is ".pdf")
        {
            PdfReportExporter.Save(result, outputPath);
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
        // 直接序列化到文件流，避免先构造完整 JSON 字符串
        using (var stream = File.Create(outputPath))
        {
            JsonSerializer.Serialize(stream, result, JsonSharedOptions.Write);
        }
        AnsiConsole.MarkupLine($"[green]报告成功保存！[/]");
    }
}
