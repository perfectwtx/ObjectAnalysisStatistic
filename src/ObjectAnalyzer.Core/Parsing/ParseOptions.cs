namespace ObjectAnalyzer.Parsing;

/// <summary>
/// Options passed to file parsers.
/// </summary>
public sealed class ParseOptions
{
    /// <summary>Last physical source line consumed (including blanks and skipped lines).</summary>
    public Action<long>? SourceProgress { get; init; }

    public bool Flatten { get; init; } = true;
    public IReadOnlyList<ObjectAnalyzer.Analysis.ValueParserConfig> ValueParsers { get; init; } = Array.Empty<ObjectAnalyzer.Analysis.ValueParserConfig>();

    /// <summary>蓄水池随机抽样：均匀随机保留最多 N 条记录参与统计（>0 启用）。由行/记录级解析器在解析前跳过未选中记录。</summary>
    public int SampleReservoir { get; init; }

    /// <summary>等距系统抽样：每 step 条记录取 1 条参与统计（>1 启用）。由行/记录级解析器跳过未选中记录。</summary>
    public int SampleStep { get; init; }

    /// <summary>
    /// P6.2：从指定的源位置开始解析（--resume 续跑）。具体语义由 parser 决定：
    /// - 行/记录级格式（jsonl/csv/xml/yaml）：跳过前 N 行
    /// - 整体 JSON：在数组中跳过前 N 个元素
    /// - Excel：跳过前 N 行
    /// 0 或负值表示从开头解析。
    /// </summary>
    public long StartFromLine { get; init; }

    /// <summary>
    /// P6.2：JSONPath 投影（在每条记录上应用，从原对象提取子节点）。由 FileParsers 在分发到具体 parser 之前应用。
    /// </summary>
    public string? RecordPath { get; init; }

    /// <summary>
    /// CSV 是否为「看起来是数字」的字符串推断数值类型（默认 true）。
    /// CSV 无类型信息，不推断的话数值列的 primaryType 永远是 String，
    /// 均值/标准差/分位数/相关性等一整套数值剖析会失效。
    /// 关掉可保留纯字符串行为（例如某列的确需要把 "007" 当文本统计）。
    /// 仅作用于 CSV —— xlsx 单元格本身带类型，不做推断。
    /// </summary>
    public bool CsvInferNumbers { get; init; } = true;
}
