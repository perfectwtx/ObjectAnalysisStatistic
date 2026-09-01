namespace ObjectAnalyzer.Analysis.Options;

/// <summary>
/// 规则文件：路径 + 未知键策略。
/// </summary>
public sealed class RulesOptions
{
    /// <summary>规则文件路径（--rules / -r）。</summary>
    public string? RulesFile { get; set; }

    /// <summary>规则文件里出现不认识的键（拼错 / 已废弃）时仍然继续分析（--allow-unknown-rules）。默认 false。</summary>
    public bool AllowUnknownRules { get; set; }
}
