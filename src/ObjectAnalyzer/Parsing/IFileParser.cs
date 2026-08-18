using System;
using System.Collections.Generic;

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
    /// Parse the file and return a collection of property dictionaries.
    /// May return a lazy enumerable (e.g., JSONL 逐行流式读取).
    /// </summary>
    IEnumerable<Dictionary<string, object?>> Parse(string path, ParseOptions options);
}
