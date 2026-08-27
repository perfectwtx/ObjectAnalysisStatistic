using System;
using System.Collections.Generic;
using System.IO;

namespace ObjectAnalyzer.Parsing;

/// <summary>
/// Options passed to file parsers.
/// </summary>
public sealed class ParseOptions
{
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
}

/// <summary>
/// Interface for file format parsers. Implement this to add new input formats.
/// </summary>
public interface IFileParser
{
    /// <summary>Format names this parser handles (e.g., "json", "csv").</summary>
    IReadOnlyList<string> SupportedFormats { get; }

    /// <summary>File extensions this parser handles (e.g., ".json", ".csv").</summary>
    IReadOnlyList<string> SupportedExtensions { get; }

    /// <summary>
    /// Parse the (already decompressed) input stream and return a collection of property dictionaries.
    /// May return a lazy enumerable (e.g., JSONL 逐行流式读取).
    /// </summary>
    IEnumerable<Dictionary<string, object?>> Parse(Stream stream, ParseOptions options);
}
