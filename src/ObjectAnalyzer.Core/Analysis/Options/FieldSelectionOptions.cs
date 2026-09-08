namespace ObjectAnalyzer.Analysis.Options;

/// <summary>
/// 字段选择 / 过滤：仅统计指定字段 + 记录级过滤表达式。
/// </summary>
public sealed class FieldSelectionOptions
{
    /// <summary>仅统计这些字段（逗号分隔，对应 <c>--fields</c>）。null / 空 = 不限。</summary>
    public string? Fields { get; set; }

    /// <summary>记录级过滤表达式（<c>==/!=/&gt;/&lt;</c> 等 + <c>in/like</c>，对应 <c>--where</c>）。null / 空 = 不过滤。</summary>
    public string? Filter { get; set; }
}
