using System.Collections.Generic;

namespace ObjectAnalyzer.Api;

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
