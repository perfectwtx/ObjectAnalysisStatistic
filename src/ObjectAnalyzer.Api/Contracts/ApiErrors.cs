using System.Collections.Generic;
using ObjectAnalyzer.Analysis.Rules;

namespace ObjectAnalyzer.Api;

/// <summary>响应构造助手：把分析入口的异常/校验错误转成统一 <see cref="ErrorResponse"/>。</summary>
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
