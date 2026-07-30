using System;
using System.Collections.Generic;

namespace ObjectAnalyzer;

/// <summary>
/// Options passed to file parsers.
/// </summary>
public sealed class ParseOptions
{
    public bool Flatten { get; init; } = true;
    public string ExcelJsonColumn { get; init; } = "A";
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

    /// <summary>Parse the file and return a list of property dictionaries.</summary>
    List<Dictionary<string, object?>> Parse(string path, ParseOptions options);
}
