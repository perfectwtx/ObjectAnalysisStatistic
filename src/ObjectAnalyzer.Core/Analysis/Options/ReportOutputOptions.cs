namespace ObjectAnalyzer.Analysis.Options;

/// <summary>
/// 多格式报告输出：主报告 + 违规清单 + 质量报告 + Schema 代码生成。
/// </summary>
public sealed class ReportOutputOptions
{
    /// <summary>主报告输出路径（按扩展名路由 .json / .html / .md / .csv / .xlsx / .pdf，-o）。</summary>
    public string? OutputPath { get; set; }

    /// <summary>结构化违规清单 JSON 输出路径（--violations）。</summary>
    public string? ViolationsPath { get; set; }

    /// <summary>数据质量报告 JSON 输出路径（--quality）。</summary>
    public string? QualityPath { get; set; }

    /// <summary>Schema 代码生成格式（csharp / typescript / json-schema / openapi / openapi-yaml / proto / sql，可带路径 "csharp:out.cs"，-s）。</summary>
    public string? ExportSchema { get; set; }

    /// <summary>Schema 代码生成目标文件路径（从 <see cref="ExportSchema"/> 的 "format:path" 形式中拆出）。</summary>
    public string? SchemaOutputPath { get; set; }
}
