using System.Collections.Generic;
using ObjectAnalyzer.Analysis;
using ObjectAnalyzer.Analysis.Rules;

namespace ObjectAnalyzer.Api;

// ── 强类型 API 响应契约 ──
// 替代端点里 Results.Ok(new { 匿名对象 }) 的写法：为每个接口定义具名 DTO，
// 使 OpenAPI / Scalar 能生成完整、可读的响应 schema（而非统一的匿名 {}）。
// 序列化沿用全局 camelCase + 省略 null 策略，JSON 契约与改动前保持一致。
//
// 说明：字段注释用属性级 XML <summary>（与 Core 的 AnalysisResult 同款），
// 因为只有成员上的文档注释会被 GenerateDocumentationFile + OpenAPI 读进 schema
// 的 description；positional record 的参数没有独立 XML 目标，会导致字段无注释。

/// <summary>GET /api/health 响应。</summary>
public sealed record HealthResponse(string Status, string? Version)
{
    /// <summary>服务状态，固定为 "ok"。</summary>
    public string Status { get; init; } = Status;

    /// <summary>核心分析库的程序集版本号。</summary>
    public string? Version { get; init; } = Version;
}

/// <summary>规则校验中「写了但后端不认识的键」单条条目。</summary>
public sealed record RulesLintEntry(string Path, string Name, string? Suggestion)
{
    /// <summary>未知键所在的 JSON 路径，如 fields.age。</summary>
    public string Path { get; init; } = Path;

    /// <summary>未知的键名。</summary>
    public string Name { get; init; } = Name;

    /// <summary>按编辑距离猜出的正确键名（没有则省略）。</summary>
    public string? Suggestion { get; init; } = Suggestion;
}

/// <summary>POST /api/rules/validate 响应（200 与 400 共用同一结构）。</summary>
public sealed record RulesValidationResponse(bool Valid, int Fields, IReadOnlyList<RulesLintEntry> Unknowns, string? Error)
{
    /// <summary>规则是否校验通过（strict 模式下存在未知键即 false）。</summary>
    public bool Valid { get; init; } = Valid;

    /// <summary>成功反序列化后识别的字段规则数量。</summary>
    public int Fields { get; init; } = Fields;

    /// <summary>后端不认识的键清单（拼写错误或已废弃）。</summary>
    public IReadOnlyList<RulesLintEntry> Unknowns { get; init; } = Unknowns;

    /// <summary>校验失败时的中文错误说明；校验通过时省略。</summary>
    public string? Error { get; init; } = Error;
}

/// <summary>通用错误响应（分析类端点返回 400 时使用）。</summary>
public sealed record ErrorResponse(string Error, IReadOnlyList<RulesLintEntry>? Unknowns = null)
{
    /// <summary>错误说明。</summary>
    public string Error { get; init; } = Error;

    /// <summary>出错时附带的后端未知键清单（仅规则相关错误包含）。</summary>
    public IReadOnlyList<RulesLintEntry>? Unknowns { get; init; } = Unknowns;
}

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

// ── 响应构造助手：把分析入口的异常/校验错误转成统一 ErrorResponse ──
internal static class ApiErrors
{
    /// <summary>构造「规则存在未知键」的 400 错误。</summary>
    public static ErrorResponse UnknownRules(List<UnknownRuleKey> unknowns) =>
        new(RulesLint.Describe(unknowns), unknowns.Select(ToEntry).ToList());

    /// <summary>构造「通用异常」的 400 错误。</summary>
    public static ErrorResponse FromException(Exception ex) =>
        new(ex.Message);

    private static RulesLintEntry ToEntry(UnknownRuleKey k) =>
        new(k.Path, k.Name, k.Suggestion);
}
