using System;
using System.Collections.Generic;
using System.IO;

namespace ObjectAnalyzer.Parsing;

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
