using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;
using MiniExcelLibs;
using YamlDotNet.Serialization;

namespace ObjectAnalyzer;

/// <summary>
/// Multi-format file parsers. Each parser reads a file and returns
/// a collection of property dictionaries for analysis.
/// </summary>
public static class FileParsers
{
    // ─────────────────────────────────────────────
    //  Parser registry — add new formats here
    // ─────────────────────────────────────────────

    private static readonly List<IFileParser> _parsers = new()
    {
        new JsonFileParser(),
        new JsonlFileParser(),
        new ExcelCsvFileParser(),
        new XmlFileParser(),
        new YamlFileParser(),
        new ExcelJsonFileParser(),
    };

    /// <summary>
    /// Detects the format name from a file extension.
    /// </summary>
    public static string DetectFormat(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        foreach (var parser in _parsers)
        {
            if (parser.SupportedExtensions.Contains(ext))
                return parser.SupportedFormats[0];
        }
        // Fallbacks
        if (ext is ".txt" or ".ndjson") return "jsonl";
        return "json";
    }

    /// <summary>
    /// Parses a file by format name using the parser registry.
    /// Format names are matched case-insensitively.
    /// </summary>
    public static IEnumerable<Dictionary<string, object?>> ParseByFormat(string path, string format, ParseOptions options)
    {
        if (string.IsNullOrWhiteSpace(format))
            throw new InvalidOperationException("未指定文件格式。");

        string normalized = format.Trim().ToLowerInvariant();
        foreach (var parser in _parsers)
        {
            if (parser.SupportedFormats.Contains(normalized))
                return parser.Parse(path, options);
        }
        throw new InvalidOperationException($"不支持的文件格式: {format}");
    }

    // ─────────────────────────────────────────────
    //  Individual format parsers (IFileParser)
    // ─────────────────────────────────────────────

    private sealed class JsonFileParser : IFileParser
    {
        public IReadOnlyList<string> SupportedFormats => new[] { "json" };
        public IReadOnlyList<string> SupportedExtensions => new[] { ".json" };

        public IEnumerable<Dictionary<string, object?>> Parse(string path, ParseOptions options)
        {
            var result = new List<Dictionary<string, object?>>();

            using var fs = File.OpenRead(path);
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(fs);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("JSON 文件解析失败，请确认文件内容为合法的 JSON。", ex);
            }

            using (doc)
            {
                var root = doc.RootElement;

                if (root.ValueKind == JsonValueKind.Array)
                {
                    foreach (var element in root.EnumerateArray())
                        result.Add(ParseJsonElement(element, options.Flatten));
                }
                else if (root.ValueKind == JsonValueKind.Object)
                {
                    result.Add(ParseJsonElement(root, options.Flatten));
                }
                else
                {
                    throw new InvalidDataException("JSON 根节点必须是数组或对象。");
                }
            }

            return result;
        }
    }

    private sealed class JsonlFileParser : IFileParser
    {
        public IReadOnlyList<string> SupportedFormats => new[] { "jsonl" };
        public IReadOnlyList<string> SupportedExtensions => new[] { ".jsonl", ".ndjson" };

        // 惰性逐行流式解析：超大 JSONL 文件无需一次性物化到内存
        public IEnumerable<Dictionary<string, object?>> Parse(string path, ParseOptions options)
        {
            using var fs = File.OpenRead(path);
            using var reader = new StreamReader(fs);
            string? line;
            int lineNumber = 0;
            while ((line = reader.ReadLine()) != null)
            {
                lineNumber++;
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(line);
                }
                catch (JsonException ex)
                {
                    throw new InvalidDataException($"在第 {lineNumber} 行解析 JSON 时出错: {ex.Message}", ex);
                }

                using (doc)
                {
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object)
                        throw new InvalidDataException($"第 {lineNumber} 行的 JSON 根节点必须是一个对象。");
                    yield return ParseJsonElement(root, options.Flatten);
                }
            }
        }
    }

    private sealed class ExcelCsvFileParser : IFileParser
    {
        public IReadOnlyList<string> SupportedFormats => new[] { "csv", "excel" };
        public IReadOnlyList<string> SupportedExtensions => new[] { ".csv", ".xlsx" };

        public IEnumerable<Dictionary<string, object?>> Parse(string path, ParseOptions options)
        {
            var result = new List<Dictionary<string, object?>>();

            var rows = MiniExcel.Query(path, useHeaderRow: true);
            foreach (var row in rows)
            {
                var dict = new Dictionary<string, object?>();
                if (row is IDictionary<string, object?> rowDict)
                {
                    foreach (var kvp in rowDict)
                    {
                        if (kvp.Key != null)
                            dict[kvp.Key] = kvp.Value;
                    }
                }
                result.Add(dict);
            }

            return result;
        }
    }

    private sealed class XmlFileParser : IFileParser
    {
        public IReadOnlyList<string> SupportedFormats => new[] { "xml" };
        public IReadOnlyList<string> SupportedExtensions => new[] { ".xml" };

        public IEnumerable<Dictionary<string, object?>> Parse(string path, ParseOptions options)
        {
            var result = new List<Dictionary<string, object?>>();

            XDocument doc;
            try
            {
                using var fs = File.OpenRead(path);
                doc = XDocument.Load(fs);
            }
            catch (InvalidDataException) { throw; }
            catch (Exception ex)
            {
                throw new InvalidDataException($"解析 XML 文件时出错: {ex.Message}", ex);
            }

            if (doc.Root == null)
                throw new InvalidDataException("XML 文件缺少根节点。");

            foreach (var element in doc.Root.Elements())
                result.Add(ConvertXElement(element));

            return result;
        }
    }

    private sealed class YamlFileParser : IFileParser
    {
        public IReadOnlyList<string> SupportedFormats => new[] { "yaml" };
        public IReadOnlyList<string> SupportedExtensions => new[] { ".yaml", ".yml" };

        public IEnumerable<Dictionary<string, object?>> Parse(string path, ParseOptions options)
        {
            var result = new List<Dictionary<string, object?>>();
            var deserializer = new DeserializerBuilder().Build();

            using var fs = File.OpenRead(path);
            using var reader = new StreamReader(fs);
            object? yamlResult = deserializer.Deserialize(reader);

            if (yamlResult is List<object?> list)
            {
                foreach (var item in list)
                {
                    if (item is Dictionary<object, object> dict)
                        result.Add(ConvertYamlDict(dict));
                }
            }
            else if (yamlResult is Dictionary<object, object> singleDict)
            {
                result.Add(ConvertYamlDict(singleDict));
            }
            else
            {
                throw new InvalidDataException("YAML 根节点必须是对象列表或单个对象。");
            }

            return result;
        }
    }

    private sealed class ExcelJsonFileParser : IFileParser
    {
        public IReadOnlyList<string> SupportedFormats => new[] { "excel-json" };
        public IReadOnlyList<string> SupportedExtensions => Array.Empty<string>(); // 无自动检测扩展名，需显式指定 --format

        public IEnumerable<Dictionary<string, object?>> Parse(string path, ParseOptions options)
        {
            var result = new List<Dictionary<string, object?>>();
            string columnName = options.ExcelJsonColumn.ToUpperInvariant();

            var rows = MiniExcel.Query(path, useHeaderRow: false);
            int rowIndex = 0;

            foreach (var row in rows)
            {
                rowIndex++;

                if (row is not IDictionary<string, object?> rowDict)
                    continue;

                if (!rowDict.TryGetValue(columnName, out object? cellValue) || cellValue == null)
                    continue;

                string jsonString = cellValue.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(jsonString))
                    continue;

                // 跳过非 JSON 对象的行（如表头行）
                string trimmed = jsonString.TrimStart();
                if (!trimmed.StartsWith('{'))
                    continue;

                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(jsonString);
                }
                catch (JsonException ex)
                {
                    throw new InvalidDataException($"在第 {rowIndex} 行解析 JSON 时出错 (列 {columnName}): {ex.Message}", ex);
                }

                using (doc)
                {
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object)
                        throw new InvalidDataException($"第 {rowIndex} 行 (列 {columnName}) 的 JSON 根节点必须是一个对象。");
                    result.Add(ParseJsonElement(root, options.Flatten));
                }
            }

            return result;
        }
    }

    // ─────────────────────────────────────────────
    //  Shared JSON helpers
    // ─────────────────────────────────────────────

    private static Dictionary<string, object?> ParseJsonElement(JsonElement element, bool flatten)
    {
        var target = new Dictionary<string, object?>();
        if (flatten)
        {
            FlattenJson(element, "", target);
        }
        else
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                    target[property.Name] = ConvertJsonElement(property.Value);
            }
        }
        return target;
    }

    private static void FlattenJson(JsonElement element, string prefix, Dictionary<string, object?> target)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    string name = string.IsNullOrEmpty(prefix) ? property.Name : $"{prefix}.{property.Name}";
                    FlattenJson(property.Value, name, target);
                }
                break;

            case JsonValueKind.Array:
                int index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    string name = $"{prefix}[{index}]";
                    FlattenJson(item, name, target);
                    index++;
                }
                break;

            case JsonValueKind.String:
                target[prefix] = element.GetString();
                break;

            case JsonValueKind.Number:
                if (element.TryGetInt64(out long l))
                    target[prefix] = l;
                else if (element.TryGetDouble(out double d))
                    target[prefix] = d;
                else
                    target[prefix] = element.GetRawText();
                break;

            case JsonValueKind.True:
                target[prefix] = true;
                break;

            case JsonValueKind.False:
                target[prefix] = false;
                break;

            case JsonValueKind.Null:
                target[prefix] = null;
                break;

            default:
                target[prefix] = element.GetRawText();
                break;
        }
    }

    private static object? ConvertJsonElement(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var dict = new Dictionary<string, object?>();
                foreach (var property in element.EnumerateObject())
                    dict[property.Name] = ConvertJsonElement(property.Value);
                return dict;

            case JsonValueKind.Array:
                var list = new List<object?>();
                foreach (var item in element.EnumerateArray())
                    list.Add(ConvertJsonElement(item));
                return list;

            case JsonValueKind.String:
                return element.GetString();

            case JsonValueKind.Number:
                if (element.TryGetInt64(out long l)) return l;
                if (element.TryGetDouble(out double d)) return d;
                return element.GetRawText();

            case JsonValueKind.True:
                return true;

            case JsonValueKind.False:
                return false;

            case JsonValueKind.Null:
                return null;

            default:
                return element.GetRawText();
        }
    }

    private static Dictionary<string, object?> ConvertXElement(XElement element)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var child in element.Elements())
        {
            string key = child.Name.LocalName;
            if (child.HasElements)
                dict[key] = ConvertXElement(child);
            else
                dict[key] = child.Value;
        }
        return dict;
    }

    private static Dictionary<string, object?> ConvertYamlDict(Dictionary<object, object> yamlDict)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var kvp in yamlDict)
        {
            string key = kvp.Key?.ToString() ?? "";
            dict[key] = ConvertYamlValue(kvp.Value);
        }
        return dict;
    }

    private static object? ConvertYamlValue(object? value)
    {
        return value switch
        {
            null => null,
            Dictionary<object, object> d => ConvertYamlDict(d),
            List<object?> list => list.Select(ConvertYamlValue).ToList(),
            _ => value
        };
    }
}
