namespace ObjectAnalyzer.Api;

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
