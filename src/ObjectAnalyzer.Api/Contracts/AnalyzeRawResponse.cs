using ObjectAnalyzer.Analysis;

namespace ObjectAnalyzer.Api;

/// <summary>POST /api/analyze/raw 成功响应（请求体即数据文本）。</summary>
public sealed record AnalyzeRawResponse(string Format, string? RulesSource, AnalysisResult Result)
{
    /// <summary>识别到的数据格式（如 json / jsonl / csv / excel / xml / yaml）。</summary>
    public string Format { get; init; } = Format;

    /// <summary>规则来源：inline；本请求未提供规则时省略。</summary>
    public string? RulesSource { get; init; } = RulesSource;

    /// <summary>完整分析结果（字段统计、类型推断、值分布、质量违规等）。</summary>
    public AnalysisResult Result { get; init; } = Result;
}
