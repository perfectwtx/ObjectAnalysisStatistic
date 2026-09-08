using System.Text.Json;

namespace ObjectAnalyzer.Reporting;

/// <summary>
/// W4 导出注册表：一种报告格式一个实现。新增格式 = 注册新实现，不再改 ReportExporter 的 if/else 链。
/// <para>
/// 实现方职责：把 <paramref name="result"/>（已按需打码）写到 <paramref name="outputPath"/>。
/// PII 打码由路由器统一执行，实现方无需关心。
/// </para>
/// </summary>
public interface IResultExporter
{
    /// <summary>本实现处理的报告格式名（如 "html"、"markdown"；JSON 兜底导出器为 "json"）。</summary>
    string Format { get; }

    /// <summary>该实现是否处理此输出路径（通常按扩展名判断）。</summary>
    bool CanHandle(string outputPath);

    /// <summary>把结果写入输出路径。</summary>
    void Save(AnalysisResult result, string outputPath);
}

/// <summary>
/// 报告导出路由器（W4）：按注册顺序匹配 <see cref="IResultExporter.CanHandle"/>，
/// 无匹配时回退 JSON 导出。注册表静态初始化，覆盖既有全部格式。
/// </summary>
public static class ReportExporter
{
    private static readonly List<IResultExporter> s_exporters =
    [
        new HtmlResultExporter(),
        new MarkdownResultExporter(),
        new CsvResultExporter(),
        new ExcelResultExporter(),
        new PdfResultExporter(),
    ];

    /// <summary>注册自定义导出器（插在队首，先注册先匹配；JSON 兜底始终最后）。</summary>
    public static void Register(IResultExporter exporter) => s_exporters.Insert(0, exporter);

    public static void Save(AnalysisResult result, string outputPath, bool redactPii = true)
    {
        // 敏感字段值统一打码后再输出，保证任何报告格式都不会泄露原始 PII（--pii-off 可显式关闭）
        result = PiiRedactor.Redact(result, redactPii);

        var exporter = s_exporters.FirstOrDefault(e => e.CanHandle(outputPath)) ?? JsonResultExporter.Instance;
        exporter.Save(result, outputPath);
    }

    /// <summary>JSON 兜底导出（默认格式，无扩展名匹配时使用）。</summary>
    private sealed class JsonResultExporter : IResultExporter
    {
        public static readonly JsonResultExporter Instance = new();

        public string Format => "json";
        public bool CanHandle(string outputPath) => true;

        public void Save(AnalysisResult result, string outputPath)
        {
            AnalysisServices.Current.Console.Write($"正在将详细分析报告保存至: [blue]{MarkupText.Escape(outputPath)}[/] ...");
            AnalysisServices.Current.Console.WriteLine();
            using (var stream = File.Create(outputPath))
            {
                JsonSerializer.Serialize(stream, result, JsonSharedOptions.Write);
            }
            AnalysisServices.Current.Console.MarkupLine($"[green]报告成功保存！[/]");
        }
    }

    private sealed class HtmlResultExporter : IResultExporter
    {
        public string Format => "html";
        public bool CanHandle(string outputPath) => Path.GetExtension(outputPath).ToLowerInvariant() is ".html" or ".htm";
        public void Save(AnalysisResult result, string outputPath) => HtmlReportExporter.Save(result, outputPath);
    }

    private sealed class MarkdownResultExporter : IResultExporter
    {
        public string Format => "markdown";
        public bool CanHandle(string outputPath) => Path.GetExtension(outputPath).ToLowerInvariant() is ".md" or ".markdown";
        public void Save(AnalysisResult result, string outputPath) => MarkdownReportExporter.Save(result, outputPath);
    }

    private sealed class CsvResultExporter : IResultExporter
    {
        public string Format => "csv";
        public bool CanHandle(string outputPath) => string.Equals(Path.GetExtension(outputPath), ".csv", StringComparison.OrdinalIgnoreCase);
        public void Save(AnalysisResult result, string outputPath) => CsvReportExporter.Save(result, outputPath);
    }

    private sealed class ExcelResultExporter : IResultExporter
    {
        public string Format => "excel";
        public bool CanHandle(string outputPath) => string.Equals(Path.GetExtension(outputPath), ".xlsx", StringComparison.OrdinalIgnoreCase);
        public void Save(AnalysisResult result, string outputPath) => ExcelReportExporter.Save(result, outputPath);
    }

    private sealed class PdfResultExporter : IResultExporter
    {
        public string Format => "pdf";
        public bool CanHandle(string outputPath) => string.Equals(Path.GetExtension(outputPath), ".pdf", StringComparison.OrdinalIgnoreCase);
        public void Save(AnalysisResult result, string outputPath) => PdfReportExporter.Save(result, outputPath);
    }
}
