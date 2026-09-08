using ObjectAnalyzer.Analysis;

namespace ObjectAnalyzer.Api;

/// <summary>POST /api/analyze 成功响应（multipart 上传文件）。</summary>
public sealed record AnalyzeResponse(string FileName, string Format, string? RulesSource, AnalysisResult Result)
{
    /// <summary>上传的数据文件名。</summary>
    public string FileName { get; init; } = FileName;

    /// <summary>识别到的数据格式（如 json / jsonl / csv / excel / xml / yaml）。</summary>
    public string Format { get; init; } = Format;

    /// <summary>规则来源：inline / file；本请求未提供规则时省略。</summary>
    public string? RulesSource { get; init; } = RulesSource;

    /// <summary>完整分析结果（字段统计、类型推断、值分布、质量违规等）。</summary>
    public AnalysisResult Result { get; init; } = Result;
}
