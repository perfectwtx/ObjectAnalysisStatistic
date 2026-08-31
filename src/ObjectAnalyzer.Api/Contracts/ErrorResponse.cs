using System.Collections.Generic;

namespace ObjectAnalyzer.Api;

/// <summary>通用错误响应（分析类端点返回 400 时使用）。</summary>
public sealed record ErrorResponse(string Error, IReadOnlyList<RulesLintEntry>? Unknowns = null)
{
    /// <summary>错误说明。</summary>
    public string Error { get; init; } = Error;

    /// <summary>出错时附带的后端未知键清单（仅规则相关错误包含）。</summary>
    public IReadOnlyList<RulesLintEntry>? Unknowns { get; init; } = Unknowns;
}
