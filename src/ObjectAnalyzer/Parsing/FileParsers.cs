using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using MiniExcelLibs;
using ObjectAnalyzer.Analysis;
using YamlDotNet.Serialization;

namespace ObjectAnalyzer.Parsing;

/// <summary>
/// Multi-format file parsers. Each parser reads a file and returns
/// a collection of property dictionaries for analysis.
/// All parsers stream results lazily via yield return to ensure minimal memory footprint.
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
            // 流式解析：每个元素独立 JsonDocument，生命周期很短，
            // 避免整个 JSON DOM 与分析过程同时常驻内存。
            byte[] fileBytes = File.ReadAllBytes(path);
            // 剥离 UTF-8 BOM，避免 Utf8JsonReader 将 BOM 当作非法字节
            // （Windows 工具/Excel 保存的 JSON 常带 BOM）
            fileBytes = StripUtf8Bom(fileBytes);
            foreach (var item in ParseFromBytes(fileBytes, options))
                yield return item;
        }

        private static byte[] StripUtf8Bom(byte[] bytes)
        {
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                return bytes.AsSpan(3).ToArray();
            }
            return bytes;
        }

        private static IEnumerable<Dictionary<string, object?>> ParseFromBytes(byte[] fileBytes, ParseOptions options)
        {
            JsonTokenType rootToken = PeekRootToken(fileBytes);

            if (rootToken == JsonTokenType.StartArray)
            {
                // 先消费根 '[',否则 TryParseValue 会把整个数组当作一个元素
                long offset = ConsumeSingleToken(fileBytes);
                // 空数组也合法（产出 0 个对象）
                while (TryReadNextElement(fileBytes, ref offset, out var elementDoc))
                {
                    using (elementDoc)
                    {
                        yield return ParseJsonElement(elementDoc.RootElement, options.Flatten);
                    }
                }
            }
            else if (rootToken == JsonTokenType.StartObject)
            {
                long offset = 0;
                if (!TryReadNextElement(fileBytes, ref offset, out var singleDoc))
                    throw new InvalidDataException("JSON 根节点必须是数组或对象。");
                using (singleDoc)
                {
                    yield return ParseJsonElement(singleDoc.RootElement, options.Flatten);
                }
            }
            else
            {
                throw new InvalidDataException("JSON 根节点必须是数组或对象。");
            }
        }

        private static JsonTokenType PeekRootToken(byte[] fileBytes)
        {
            try
            {
                var reader = new Utf8JsonReader(fileBytes);
                while (reader.Read())
                {
                    return reader.TokenType;
                }
                return JsonTokenType.None;
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("JSON 文件解析失败，请确认文件内容为合法的 JSON。", ex);
            }
        }

        /// <summary>
        /// 从 offset 处读取下一个顶层 JSON 值（跳过空白与逗号）。
        /// 返回 false 表示遇到数组/对象结束符。每个元素的 reader 都是局部的，
        /// 避免 ref struct 跨 yield 边界。
        /// </summary>
        /// <summary>
        /// 从开头读取一个顶层 token（如根 '['）并返回其结束偏移。
        /// </summary>
        private static long ConsumeSingleToken(byte[] fileBytes)
        {
            var reader = new Utf8JsonReader(fileBytes);
            try
            {
                if (!reader.Read())
                    throw new InvalidDataException("JSON 文件为空，缺少根 token。");
                return reader.BytesConsumed;
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("JSON 文件解析失败，请确认文件内容为合法的 JSON。", ex);
            }
        }

        private static bool TryReadNextElement(byte[] fileBytes, ref long offset, out JsonDocument doc)
        {
            // 跳过元素间的分隔逗号与空白（TryParseValue 的 BytesConsumed 停在逗号前）
            while (offset < fileBytes.Length)
            {
                byte b = fileBytes[offset];
                if (b == (byte)' ' || b == (byte)'\t' || b == (byte)'\n' || b == (byte)'\r' || b == (byte)',')
                    offset++;
                else
                    break;
            }

            if (offset >= fileBytes.Length || fileBytes[offset] == (byte)']' || fileBytes[offset] == (byte)'}')
            {
                doc = null!;
                return false;
            }

            var reader = new Utf8JsonReader(fileBytes.AsSpan((int)offset));
            try
            {
                while (reader.Read())
                {
                    switch (reader.TokenType)
                    {
                        case JsonTokenType.EndArray:
                        case JsonTokenType.EndObject:
                            offset += reader.BytesConsumed;
                            doc = null!;
                            return false;

                        case JsonTokenType.StartObject:
                        case JsonTokenType.StartArray:
                        case JsonTokenType.String:
                        case JsonTokenType.Number:
                        case JsonTokenType.True:
                        case JsonTokenType.False:
                        case JsonTokenType.Null:
                            if (JsonDocument.TryParseValue(ref reader, out var parsed))
                            {
                                offset += reader.BytesConsumed;
                                doc = parsed;
                                return true;
                            }
                            throw new InvalidDataException("JSON 值解析失败。");
                    }
                }
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("JSON 文件解析失败，请确认文件内容为合法的 JSON。", ex);
            }

            offset = fileBytes.Length;
            doc = null!;
            return false;
        }
    }

    private sealed class JsonlFileParser : IFileParser
    {
        public IReadOnlyList<string> SupportedFormats => new[] { "jsonl" };
        public IReadOnlyList<string> SupportedExtensions => new[] { ".jsonl", ".ndjson" };

        // 惰性逐行流式解析：超大 JSONL 文件无需一次性物化到内存。
        // 采样模式下（--sample-reservoir / --sample-step）仅在选中行上做 JSON 解析，
        // 未选中行直接跳过，从而在巨型文件上数秒内完成 Schema 概览。
        public IEnumerable<Dictionary<string, object?>> Parse(string path, ParseOptions options)
        {
            if (options.SampleReservoir > 0)
                return ParseReservoir(path, options);
            if (options.SampleStep > 0)
                return ParseStep(path, options);
            return ParseAll(path, options);
        }

        private static IEnumerable<Dictionary<string, object?>> ParseAll(string path, ParseOptions options)
        {
            using var reader = new StreamReader(File.OpenRead(path));
            string? line;
            long lineNumber = 0;
            while ((line = reader.ReadLine()) != null)
            {
                lineNumber++;
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                yield return ParseJsonLine(line, lineNumber, options);
            }
        }

        private static IEnumerable<Dictionary<string, object?>> ParseReservoir(string path, ParseOptions options)
        {
            var sampler = new ReservoirSampler<Dictionary<string, object?>>(options.SampleReservoir);
            using var reader = new StreamReader(File.OpenRead(path));
            string? line;
            long lineNumber = 0;
            while ((line = reader.ReadLine()) != null)
            {
                lineNumber++;
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                int slot = sampler.NextSlot();
                if (slot < 0)
                    continue; // 未命中槽位：跳过解析，只付出 I/O 成本
                sampler.SetSlot(slot, ParseJsonLine(line, lineNumber, options));
            }
            return sampler.Items;
        }

        private static IEnumerable<Dictionary<string, object?>> ParseStep(string path, ParseOptions options)
        {
            using var reader = new StreamReader(File.OpenRead(path));
            string? line;
            long lineNumber = 0;
            long recordIndex = 0;
            while ((line = reader.ReadLine()) != null)
            {
                lineNumber++;
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                if (recordIndex++ % options.SampleStep != 0)
                    continue; // 未命中步长：跳过解析
                yield return ParseJsonLine(line, lineNumber, options);
            }
        }

        private static Dictionary<string, object?> ParseJsonLine(string line, long lineNumber, ParseOptions options)
        {
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
                return ParseJsonElement(root, options.Flatten);
            }
        }
    }

    private sealed class ExcelCsvFileParser : IFileParser
    {
        public IReadOnlyList<string> SupportedFormats => new[] { "csv", "excel" };
        public IReadOnlyList<string> SupportedExtensions => new[] { ".csv", ".xlsx" };

        public IEnumerable<Dictionary<string, object?>> Parse(string path, ParseOptions options)
        {
            if (options.SampleReservoir > 0)
                return ParseReservoir(path, options);
            if (options.SampleStep > 0)
                return ParseStep(path, options);
            return ParseAll(path, options);
        }

        private static IEnumerable<Dictionary<string, object?>> ParseAll(string path, ParseOptions options)
        {
            foreach (var row in MiniExcel.Query(path, useHeaderRow: true))
            {
                var dict = ToRowDict(row);
                if (dict != null)
                    yield return dict;
            }
        }

        private static IEnumerable<Dictionary<string, object?>> ParseReservoir(string path, ParseOptions options)
        {
            var sampler = new ReservoirSampler<Dictionary<string, object?>>(options.SampleReservoir);
            foreach (var row in MiniExcel.Query(path, useHeaderRow: true))
            {
                int slot = sampler.NextSlot();
                if (slot < 0)
                    continue;
                var dict = ToRowDict(row);
                if (dict != null)
                    sampler.SetSlot(slot, dict);
            }
            return sampler.Items;
        }

        private static IEnumerable<Dictionary<string, object?>> ParseStep(string path, ParseOptions options)
        {
            long recordIndex = 0;
            foreach (var row in MiniExcel.Query(path, useHeaderRow: true))
            {
                if (recordIndex++ % options.SampleStep != 0)
                    continue;
                var dict = ToRowDict(row);
                if (dict != null)
                    yield return dict;
            }
        }

        private static Dictionary<string, object?>? ToRowDict(object? row)
        {
            if (row is not IDictionary<string, object?> rowDict)
                return null;
            var dict = new Dictionary<string, object?>();
            foreach (var kvp in rowDict)
            {
                if (kvp.Key != null)
                    dict[kvp.Key] = kvp.Value;
            }
            return dict;
        }
    }

    private sealed class XmlFileParser : IFileParser
    {
        public IReadOnlyList<string> SupportedFormats => new[] { "xml" };
        public IReadOnlyList<string> SupportedExtensions => new[] { ".xml" };

        public IEnumerable<Dictionary<string, object?>> Parse(string path, ParseOptions options)
        {
            XDocument doc;
            try
            {
                using var fs = File.OpenRead(path);
                // 显式禁止 DTD（防 XXE/实体炸弹）并限制文档大小，防止超大/恶意 XML 打满内存
                var settings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    MaxCharactersInDocument = 256 * 1024 * 1024
                };
                using var xmlReader = XmlReader.Create(fs, settings);
                doc = XDocument.Load(xmlReader);
            }
            catch (Exception ex)
            {
                throw new InvalidDataException($"解析 XML 文件时出错: {ex.Message}", ex);
            }

            if (doc.Root == null)
                throw new InvalidDataException("XML 文件缺少根节点。");

            foreach (var element in doc.Root.Elements())
                yield return ConvertXElement(element, 0);
        }
    }

    private sealed class YamlFileParser : IFileParser
    {
        public IReadOnlyList<string> SupportedFormats => new[] { "yaml" };
        public IReadOnlyList<string> SupportedExtensions => new[] { ".yaml", ".yml" };

        public IEnumerable<Dictionary<string, object?>> Parse(string path, ParseOptions options)
        {
            var deserializer = new DeserializerBuilder().Build();

            using var fs = File.OpenRead(path);
            using var reader = new StreamReader(fs);
            object? yamlResult = deserializer.Deserialize(reader);

            if (yamlResult is List<object?> list)
            {
                foreach (var item in list)
                {
                    if (item is Dictionary<object, object> dict)
                    {
                        var converted = ConvertYamlDict(dict, 0);
                        // Fix #4: respect options.Flatten for YAML (previously ignored)
                        yield return options.Flatten ? FlattenObjectDict(converted) : converted;
                    }
                }
            }
            else if (yamlResult is Dictionary<object, object> singleDict)
            {
                var converted = ConvertYamlDict(singleDict, 0);
                yield return options.Flatten ? FlattenObjectDict(converted) : converted;
            }
            else
            {
                throw new InvalidDataException("YAML 根节点必须是对象列表或单个对象。");
            }
        }
    }

    private sealed class ExcelJsonFileParser : IFileParser
    {
        public IReadOnlyList<string> SupportedFormats => new[] { "excel-json" };
        public IReadOnlyList<string> SupportedExtensions => Array.Empty<string>(); // 无自动检测扩展名，需显式指定 --format

        public IEnumerable<Dictionary<string, object?>> Parse(string path, ParseOptions options)
        {
            var sources = options.ValueParsers.Count > 0
                ? options.ValueParsers
                : BuildLegacySources(options);

            if (options.SampleReservoir > 0)
                return ParseReservoir(path, sources, options);
            if (options.SampleStep > 0)
                return ParseStep(path, sources, options);
            return ParseAll(path, sources, options);
        }

        private static List<ObjectAnalyzer.Analysis.ValueParserConfig> BuildLegacySources(ParseOptions options)
        {
            var columns = options.ExcelJsonColumns.Count > 0
                ? options.ExcelJsonColumns.Select(c => c.Trim()).Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                : new List<string> { options.ExcelJsonColumn.Trim() };

            return columns.Select(c => new ObjectAnalyzer.Analysis.ValueParserConfig { Source = c, ParseType = "json" }).ToList();
        }

        private static IEnumerable<Dictionary<string, object?>> ParseAll(string path, IReadOnlyList<ObjectAnalyzer.Analysis.ValueParserConfig> sources, ParseOptions options)
        {
            long rowIndex = 0;
            foreach (var row in MiniExcel.Query(path, useHeaderRow: false))
            {
                rowIndex++;
                var dict = TryParseValueSources(row, sources, rowIndex, options);
                if (dict != null)
                    yield return dict;
            }
        }

        // 采样：先按记录序做准入/步长判定，未选中行连 JSON 单元格都不解析
        private static IEnumerable<Dictionary<string, object?>> ParseReservoir(string path, IReadOnlyList<ObjectAnalyzer.Analysis.ValueParserConfig> sources, ParseOptions options)
        {
            var sampler = new ReservoirSampler<Dictionary<string, object?>>(options.SampleReservoir);
            foreach (var (rowIndex, row) in Indexed(MiniExcel.Query(path, useHeaderRow: false)))
            {
                int slot = sampler.NextSlot();
                if (slot < 0)
                    continue;
                var dict = TryParseValueSources(row, sources, rowIndex, options);
                if (dict != null)
                    sampler.SetSlot(slot, dict);
            }
            return sampler.Items;
        }

        private static IEnumerable<Dictionary<string, object?>> ParseStep(string path, IReadOnlyList<ObjectAnalyzer.Analysis.ValueParserConfig> sources, ParseOptions options)
        {
            long recordIndex = 0;
            foreach (var (rowIndex, row) in Indexed(MiniExcel.Query(path, useHeaderRow: false)))
            {
                if (recordIndex++ % options.SampleStep != 0)
                    continue;
                var dict = TryParseValueSources(row, sources, rowIndex, options);
                if (dict != null)
                    yield return dict;
            }
        }

        private static Dictionary<string, object?>? TryParseValueSources(object? row, IReadOnlyList<ObjectAnalyzer.Analysis.ValueParserConfig> sources, long rowIndex, ParseOptions options)
        {
            if (row is not IDictionary<string, object?> rowDict)
                return null;

            if (LooksLikeHeaderRow(rowDict, sources))
                return null;

            var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in sources)
            {
                string sourceKey = source.Source.Trim();
                if (string.IsNullOrWhiteSpace(sourceKey))
                    continue;

                object? cellValue = LookupCellValue(rowDict, sourceKey);
                if (cellValue == null)
                    continue;

                var parsedValue = ParseValueByType(cellValue, source.ParseType, sourceKey, rowIndex, options);
                if (parsedValue == null)
                    continue;

                if (options.Flatten)
                {
                    AddFlattenedParsedValue(result, sourceKey, parsedValue);
                    continue;
                }

                result[sourceKey] = parsedValue;
            }

            return result.Count > 0 ? result : null;
        }

        private static void AddFlattenedParsedValue(Dictionary<string, object?> result, string sourceKey, object? parsedValue)
        {
            if (parsedValue == null)
                return;

            switch (parsedValue)
            {
                case Dictionary<string, object?> nestedDict:
                    FlattenObjectDictInto(nestedDict, sourceKey, result, 0);
                    break;
                case IList list:
                    for (int i = 0; i < list.Count; i++)
                    {
                        string indexedKey = $"{sourceKey}[{i}]";
                        var item = list[i];
                        if (item is Dictionary<string, object?> itemDict)
                        {
                            FlattenObjectDictInto(itemDict, indexedKey, result, 0);
                        }
                        else
                        {
                            result[indexedKey] = item;
                        }
                    }
                    break;
                default:
                    result[sourceKey] = parsedValue;
                    break;
            }
        }

        private static object? ParseValueByType(object? value, string parseType, string sourceKey, long rowIndex, ParseOptions options)
        {
            string raw = value?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            var normalizedType = parseType?.Trim();
            if (string.IsNullOrWhiteSpace(normalizedType))
                normalizedType = "auto";

            normalizedType = normalizedType.ToLowerInvariant();
            return normalizedType switch
            {
                "none" => raw,
                "auto" => TryParseJsonValue(raw, out var autoValue) ? autoValue : raw,
                "json" => ParseJsonValue(raw, sourceKey, rowIndex),
                "json-string" => ParseJsonStringValue(raw, sourceKey, rowIndex),
                _ => throw new InvalidDataException($"不支持的 parseType '{parseType}'（支持: none / auto / json / json-string）。")
            };
        }

        private static object? ParseJsonStringValue(string jsonString, string sourceKey, long rowIndex)
        {
            var trimmed = jsonString.Trim();
            if (trimmed.Length == 0)
                return null;

            if (TryParseJsonValue(trimmed, out var directValue))
                return directValue;

            if (trimmed.StartsWith('"'))
            {
                using var doc = JsonDocument.Parse(trimmed);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException($"第 {rowIndex} 行 (源 {sourceKey}) 的 json-string 值必须是一个 JSON 字符串。");
                var inner = root.GetString() ?? "";
                if (string.IsNullOrWhiteSpace(inner))
                    return "";
                return TryParseJsonValue(inner, out var nested) ? nested : inner;
            }

            return ParseJsonValue(trimmed, sourceKey, rowIndex);
        }

        private static object? ParseJsonValue(string jsonString, string sourceKey, long rowIndex)
        {
            if (string.IsNullOrWhiteSpace(jsonString))
                return null;

            if (!TryParseJsonValue(jsonString, out var parsed))
                throw new InvalidDataException($"第 {rowIndex} 行 (源 {sourceKey}) 的值不是有效 JSON：{jsonString}");
            return parsed;
        }

        private static bool TryParseJsonValue(string raw, out object? value)
        {
            string trimmed = raw.Trim();
            if (trimmed.Length == 0)
            {
                value = null;
                return false;
            }

            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                value = ConvertJsonDocument(doc.RootElement);
                return true;
            }
            catch (JsonException)
            {
                value = null;
                return false;
            }
        }

        private static object? ConvertJsonDocument(JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.Object => ParseJsonElement(element, true),
                JsonValueKind.Array => element.EnumerateArray().Select(ConvertJsonDocument).ToList(),
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.TryGetDouble(out var d) ? d : element.GetRawText(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => element.GetRawText()
            };
        }

        private static bool LooksLikeHeaderRow(IDictionary<string, object?> rowDict, IReadOnlyList<ObjectAnalyzer.Analysis.ValueParserConfig> sources)
        {
            if (sources.Count == 0)
                return false;

            foreach (var source in sources)
            {
                string sourceKey = source.Source?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(sourceKey))
                    continue;

                if (!rowDict.TryGetValue(sourceKey, out var value) && !TryFindKey(rowDict, sourceKey, out value))
                    return false;

                if (value is null)
                    return false;

                if (!string.Equals(value.ToString(), sourceKey, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return true;
        }

        private static bool TryFindKey(IDictionary<string, object?> rowDict, string sourceKey, out object? value)
        {
            foreach (var kvp in rowDict)
            {
                if (string.Equals(kvp.Key, sourceKey, StringComparison.OrdinalIgnoreCase))
                {
                    value = kvp.Value;
                    return true;
                }
            }

            value = null;
            return false;
        }

        private static object? LookupCellValue(IDictionary<string, object?> rowDict, string sourceKey)
        {
            if (rowDict.TryGetValue(sourceKey, out var exact))
                return exact;

            foreach (var kvp in rowDict)
            {
                if (string.Equals(kvp.Key, sourceKey, StringComparison.OrdinalIgnoreCase))
                    return kvp.Value;
            }

            return null;
        }

        private static IEnumerable<(long Index, object? Row)> Indexed(IEnumerable<object?> rows)
        {
            long index = 0;
            foreach (var row in rows)
            {
                yield return (index++, row);
            }
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

    internal static void FlattenJson(JsonElement element, string prefix, Dictionary<string, object?> target)
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

    private static Dictionary<string, object?> ConvertXElement(XElement element, int depth)
    {
        // 防御深层嵌套导致 StackOverflowException（进程崩溃 / Web DoS）
        if (depth > 1000)
            throw new InvalidDataException("XML 嵌套层级过深，超过 1000 层限制。");

        var dict = new Dictionary<string, object?>();

        // Fix #5: include XML attributes so <item id="1" name="foo"/> fields are not lost
        foreach (var attr in element.Attributes())
        {
            dict[attr.Name.LocalName] = attr.Value;
        }

        foreach (var child in element.Elements())
        {
            string key = child.Name.LocalName;
            object? value = child.HasElements ? ConvertXElement(child, depth + 1) : child.Value;

            // 同名重复子元素聚合为列表，避免后者覆盖前者
            if (!dict.TryGetValue(key, out var existing))
            {
                dict[key] = value;
            }
            else if (existing is List<object?> list)
            {
                list.Add(value);
            }
            else
            {
                dict[key] = new List<object?> { existing, value };
            }
        }
        return dict;
    }

    private static Dictionary<string, object?> ConvertYamlDict(Dictionary<object, object> yamlDict, int depth)
    {
        // 防御深层嵌套导致 StackOverflowException（进程崩溃 / Web DoS）
        if (depth > 1000)
            throw new InvalidDataException("YAML 嵌套层级过深，超过 1000 层限制。");

        var dict = new Dictionary<string, object?>();
        foreach (var kvp in yamlDict)
        {
            string key = kvp.Key?.ToString() ?? "";
            dict[key] = ConvertYamlValue(kvp.Value, depth + 1);
        }
        return dict;
    }

    private static object? ConvertYamlValue(object? value, int depth)
    {
        if (depth > 1000)
            throw new InvalidDataException("YAML 嵌套层级过深，超过 1000 层限制。");

        return value switch
        {
            null => null,
            Dictionary<object, object> d => ConvertYamlDict(d, depth + 1),
            List<object?> list => list.Select(v => ConvertYamlValue(v, depth + 1)).ToList(),
            _ => value
        };
    }

    // Fix #4: Generic dictionary flattener for YAML (and any object-graph) output
    private static Dictionary<string, object?> FlattenObjectDict(Dictionary<string, object?> source)
    {
        var result = new Dictionary<string, object?>();
        FlattenObjectDictInto(source, "", result);
        return result;
    }

    private static void FlattenObjectDictInto(
        Dictionary<string, object?> source,
        string prefix,
        Dictionary<string, object?> target,
        int depth = 0)
    {
        // 防御深层嵌套导致 StackOverflowException
        if (depth > 1000)
            throw new InvalidDataException("对象嵌套层级过深，超过 1000 层限制。");

        foreach (var kvp in source)
        {
            string key = string.IsNullOrEmpty(prefix) ? kvp.Key : $"{prefix}.{kvp.Key}";
            switch (kvp.Value)
            {
                case Dictionary<string, object?> nested:
                    FlattenObjectDictInto(nested, key, target, depth + 1);
                    break;
                case System.Collections.IList list:
                    for (int i = 0; i < list.Count; i++)
                    {
                        string indexedKey = $"{key}[{i}]";
                        if (list[i] is Dictionary<string, object?> itemDict)
                            FlattenObjectDictInto(itemDict, indexedKey, target, depth + 1);
                        else
                            target[indexedKey] = list[i];
                    }
                    break;
                default:
                    target[key] = kvp.Value;
                    break;
            }
        }
    }
}
