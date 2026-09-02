namespace ObjectAnalyzer.Analysis;

/// <summary>
/// 预检输出：把"用错规则文件"这件事拆成结构化字段，让 UI 自由渲染。
///
/// 字段含义：
/// - <see cref="HasWarning"/> = true 表示"规则字段在数据里一个都没匹配上"（含父路径匹配）
/// - <see cref="RuleFields"/>：去重 + 排空后的规则字段名列表
/// - <see cref="MatchedRuleFields"/> / <see cref="UnmatchedRuleFields"/>：分类输出，便于 UI 用两列对比
/// - <see cref="SampleFields"/>：去重后按出现顺序取前 <see cref="PreflightChecker.MaxDisplayedSampleFields"/> 个
/// - <see cref="TruncatedSampleFieldCount"/>：被截断的样本字段数（=实际总数 - 上限）
/// </summary>
public sealed record PreflightResult(
    bool HasWarning,
    IReadOnlyList<string> RuleFields,
    IReadOnlyList<string> MatchedRuleFields,
    IReadOnlyList<string> UnmatchedRuleFields,
    IReadOnlyList<string> SampleFields,
    int TruncatedSampleFieldCount)
{
    public static PreflightResult NoWarning() =>
        new(false, Array.Empty<string>(), Array.Empty<string>(),
            Array.Empty<string>(), Array.Empty<string>(), 0);
}
