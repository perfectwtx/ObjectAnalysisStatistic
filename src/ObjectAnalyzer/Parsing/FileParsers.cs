using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
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
    /// 支持压缩包裹：gzip/bzip2 取内层扩展名；zip 取首个数据条目的扩展名。
    /// </summary>
    public static string DetectFormat(string path)
    {
        CompressionType ct = CompressionDetector.Detect(path);
        if (ct == CompressionType.None)
        {
            // 内容级兜底：未知扩展名但实为 Office Open XML 包（如被改名的 .xlsx）→ 按 Excel 解析
            if (!CompressionDetector.HasKnownDataExtension(path) &&
                CompressionDetector.LooksLikeOfficeOpenXmlPackage(path))
            {
                return "excel";
            }

            return FormatFromExtension(Path.GetExtension(path));
        }

        if (ct == CompressionType.Zip)
            return DetectZipFormat(path);

        // gzip / bzip2：内层扩展名决定格式
        return FormatFromExtension(Path.GetExtension(CompressionDetector.StripCompression(path)));
    }

    /// <summary>
    /// 仅按扩展名（已剥离压缩层）推断格式名；供 <see cref="InputSource"/> 复用，也可在测试中直接校验。
    /// </summary>
    public static string FormatFromExtension(string ext)
    {
        ext = ext.ToLowerInvariant();

        // 明确优先返回 Excel（.xlsx）而不是 CSV；CSV 与 Excel 共享同一解析器，但 .xlsx 的语义应走 excel 分支。
        if (ext == ".xlsx")
            return "excel";

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
    /// 打开 zip 中央目录，按确定性规则选取数据条目并以其（剥离嵌套 gzip/bzip2 后缀的）扩展名推断格式
    /// （探测失败则退化为 json）。
    /// </summary>
    private static string DetectZipFormat(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false);
            var entry = InputSource.PickZipEntry(zip, out _);
            if (entry == null)
                return "json";
            string innerName = CompressionDetector.StripGzipBzip2Chain(entry.FullName);
            return FormatFromExtension(Path.GetExtension(innerName));
        }
        catch
        {
            return "json";
        }
    }

    /// <summary>
    /// Parses a file by format name using the parser registry.
    /// Format names are matched case-insensitively.
    /// 自动处理 gzip / bzip2 / zip 压缩包裹：在枚举期间惰性解压并持有输入源，枚举结束后释放。
    /// </summary>
    public static IEnumerable<Dictionary<string, object?>> ParseByFormat(string path, string format, ParseOptions options)
    {
        if (string.IsNullOrWhiteSpace(format))
            throw new InvalidOperationException("未指定文件格式。");

        string normalized = format.Trim().ToLowerInvariant();
        foreach (var parser in _parsers)
        {
            if (parser.SupportedFormats.Contains(normalized))
                return new DecompressingEnumerable(path, parser, options);
        }
        throw new InvalidOperationException($"不支持的文件格式: {format}");
    }

    /// <summary>
    /// 惰性枚举包装：在迭代开始（GetEnumerator）时才打开压缩输入源，
    /// 并在外层枚举器释放时一并释放解压流与底层句柄，确保文件句柄不泄漏。
    /// </summary>
    private sealed class DecompressingEnumerable : IEnumerable<Dictionary<string, object?>>
    {
        private readonly string _path;
        private readonly IFileParser _parser;
        private readonly ParseOptions _options;

        public DecompressingEnumerable(string path, IFileParser parser, ParseOptions options)
        {
            _path = path;
            _parser = parser;
            _options = options;
        }

        public IEnumerator<Dictionary<string, object?>> GetEnumerator()
        {
            var source = InputSource.Open(_path);
            try
            {
                var inner = _parser.Parse(source.Content, _options).GetEnumerator();
                return new DisposingEnumerator(inner, source);
            }
            catch
            {
                source.Dispose();
                throw;
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        private sealed class DisposingEnumerator : IEnumerator<Dictionary<string, object?>>
        {
            private readonly IEnumerator<Dictionary<string, object?>> _inner;
            private readonly InputSource _source;

            public DisposingEnumerator(IEnumerator<Dictionary<string, object?>> inner, InputSource source)
            {
                _inner = inner;
                _source = source;
            }

            public Dictionary<string, object?> Current => _inner.Current;
            object System.Collections.IEnumerator.Current => _inner.Current;
            public bool MoveNext() => _inner.MoveNext();
            public void Reset() => _inner.Reset();
            public void Dispose()
            {
                _inner.Dispose();
                _source.Dispose();
            }
        }
    }

    // ─────────────────────────────────────────────
    //  Individual format parsers (IFileParser)
    // ─────────────────────────────────────────────

    private sealed class JsonFileParser : IFileParser
    {
        public IReadOnlyList<string> SupportedFormats => new[] { "json" };
        public IReadOnlyList<string> SupportedExtensions => new[] { ".json" };

        public IEnumerable<Dictionary<string, object?>> Parse(Stream stream, ParseOptions options)
        {
            // 流式解析：每个元素独立 JsonDocument，生命周期很短，
            // 避免整个 JSON DOM 与分析过程同时常驻内存。
            byte[] fileBytes;
            using (var ms = new MemoryStream())
            {
                stream.CopyTo(ms);
                fileBytes = ms.ToArray();
            }
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
        public IEnumerable<Dictionary<string, object?>> Parse(Stream stream, ParseOptions options)
        {
            if (options.SampleReservoir > 0)
                return ParseReservoir(stream, options);
            if (options.SampleStep > 0)
                return ParseStep(stream, options);
            return ParseAll(stream, options);
        }

        private static IEnumerable<Dictionary<string, object?>> ParseAll(Stream stream, ParseOptions options)
        {
            using var reader = new StreamReader(stream);
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

        private static IEnumerable<Dictionary<string, object?>> ParseReservoir(Stream stream, ParseOptions options)
        {
            var sampler = new ReservoirSampler<Dictionary<string, object?>>(options.SampleReservoir);
            using var reader = new StreamReader(stream);
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

        private static IEnumerable<Dictionary<string, object?>> ParseStep(Stream stream, ParseOptions options)
        {
            using var reader = new StreamReader(stream);
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
        public IReadOnlyList<string> SupportedFormats => new[] { "excel", "csv" };
        public IReadOnlyList<string> SupportedExtensions => new[] { ".csv", ".xlsx" };

        public IEnumerable<Dictionary<string, object?>> Parse(Stream stream, ParseOptions options)
        {
            if (options.SampleReservoir > 0)
                return ParseReservoir(stream, options);
            if (options.SampleStep > 0)
                return ParseStep(stream, options);
            return ParseAll(stream, options);
        }

        // 表头模式：header=0 无表头（列以字母 A/B/C 命名，首行是表头需跳过）；header=1（默认）首行值为列名。
        private static bool UseHeaderRow(ParseOptions options)
        {
            foreach (var source in options.ValueParsers)
            {
                if (source.Header is int header)
                    return header == 1;
            }
            return true;
        }

        private static IEnumerable<Dictionary<string, object?>> ParseAll(Stream stream, ParseOptions options)
        {
            long rowIndex = 0;
            bool useHeaderRow = UseHeaderRow(options);
            foreach (var row in MiniExcel.Query(stream, useHeaderRow: useHeaderRow, excelType: ResolveExcelType(stream)))
            {
                rowIndex++;
                if (!useHeaderRow && rowIndex == 1)
                    continue; // 表头模式 0：首行是表头，跳过
                var dict = ToRowDict(row);
                if (dict != null)
                    yield return dict;
            }
        }

        private static IEnumerable<Dictionary<string, object?>> ParseReservoir(Stream stream, ParseOptions options)
        {
            var sampler = new ReservoirSampler<Dictionary<string, object?>>(options.SampleReservoir);
            bool useHeaderRow = UseHeaderRow(options);
            long rowIndex = 0;
            foreach (var row in MiniExcel.Query(stream, useHeaderRow: useHeaderRow, excelType: ResolveExcelType(stream)))
            {
                rowIndex++;
                if (!useHeaderRow && rowIndex == 1)
                    continue;
                int slot = sampler.NextSlot();
                if (slot < 0)
                    continue;
                var dict = ToRowDict(row);
                if (dict != null)
                    sampler.SetSlot(slot, dict);
            }
            return sampler.Items;
        }

        private static IEnumerable<Dictionary<string, object?>> ParseStep(Stream stream, ParseOptions options)
        {
            long recordIndex = 0;
            bool useHeaderRow = UseHeaderRow(options);
            long rowIndex = 0;
            foreach (var row in MiniExcel.Query(stream, useHeaderRow: useHeaderRow, excelType: ResolveExcelType(stream)))
            {
                rowIndex++;
                if (!useHeaderRow && rowIndex == 1)
                    continue;
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

        public IEnumerable<Dictionary<string, object?>> Parse(Stream stream, ParseOptions options)
        {
            XDocument doc;
            try
            {
                // 显式禁止 DTD（防 XXE/实体炸弹）并限制文档大小，防止超大/恶意 XML 打满内存
                var settings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    MaxCharactersInDocument = 256 * 1024 * 1024
                };
                using var xmlReader = XmlReader.Create(stream, settings);
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

        public IEnumerable<Dictionary<string, object?>> Parse(Stream stream, ParseOptions options)
        {
            var deserializer = new DeserializerBuilder().Build();

            using var reader = new StreamReader(stream);
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
        public IReadOnlyList<string> SupportedExtensions => Array.Empty<string>(); // 无自动检测扩展名，需在规则或调用方显式使用 excel-json

        public IEnumerable<Dictionary<string, object?>> Parse(Stream stream, ParseOptions options)
        {
            var sources = options.ValueParsers;

            if (options.SampleReservoir > 0)
                return ParseReservoir(stream, sources, options);
            if (options.SampleStep > 0)
                return ParseStep(stream, sources, options);
            return ParseAll(stream, sources, options);
        }

        private static IEnumerable<Dictionary<string, object?>> ParseAll(Stream stream, IReadOnlyList<ObjectAnalyzer.Analysis.ValueParserConfig> sources, ParseOptions options)
        {
            long rowIndex = 0;
            bool useHeaderRow = UseHeaderRow(sources);
            foreach (var row in MiniExcel.Query(stream, useHeaderRow: useHeaderRow, excelType: ResolveExcelType(stream)))
            {
                rowIndex++;
                var dict = TryParseValueSources(row, sources, rowIndex, options, useHeaderRow);
                if (dict != null)
                    yield return dict;
            }
        }

        // 采样：先按记录序做准入/步长判定，未选中行连 JSON 单元格都不解析
        private static IEnumerable<Dictionary<string, object?>> ParseReservoir(Stream stream, IReadOnlyList<ObjectAnalyzer.Analysis.ValueParserConfig> sources, ParseOptions options)
        {
            var sampler = new ReservoirSampler<Dictionary<string, object?>>(options.SampleReservoir);
            bool useHeaderRow = UseHeaderRow(sources);
            foreach (var (rowIndex, row) in Indexed(MiniExcel.Query(stream, useHeaderRow: useHeaderRow, excelType: ResolveExcelType(stream))))
            {
                int slot = sampler.NextSlot();
                if (slot < 0)
                    continue;
                var dict = TryParseValueSources(row, sources, rowIndex, options, useHeaderRow);
                if (dict != null)
                    sampler.SetSlot(slot, dict);
            }
            return sampler.Items;
        }

        private static IEnumerable<Dictionary<string, object?>> ParseStep(Stream stream, IReadOnlyList<ObjectAnalyzer.Analysis.ValueParserConfig> sources, ParseOptions options)
        {
            long recordIndex = 0;
            bool useHeaderRow = UseHeaderRow(sources);
            foreach (var (rowIndex, row) in Indexed(MiniExcel.Query(stream, useHeaderRow: useHeaderRow, excelType: ResolveExcelType(stream))))
            {
                if (recordIndex++ % options.SampleStep != 0)
                    continue;
                var dict = TryParseValueSources(row, sources, rowIndex, options, useHeaderRow);
                if (dict != null)
                    yield return dict;
            }
        }

        // 表头模式：header=0 无表头（列以字母 A/B/C 命名，首行为数据）；header=1（默认）首行值为列名。
        private static bool UseHeaderRow(IReadOnlyList<ObjectAnalyzer.Analysis.ValueParserConfig> sources)
        {
            foreach (var source in sources)
            {
                if (source.Header is int header)
                    return header == 1;
            }
            return true;
        }

        private static Dictionary<string, object?>? TryParseValueSources(object? row, IReadOnlyList<ObjectAnalyzer.Analysis.ValueParserConfig> sources, long rowIndex, ParseOptions options, bool useHeaderRow)
        {
            if (row is not IDictionary<string, object?> rowDict)
                return null;

            // 表头模式 0：MiniExcel 无表头读取（列键为 A/B/C），首行是表头，手动跳过
            if (!useHeaderRow && rowIndex == 1)
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

                bool shouldFlatten = source.Flatten ?? options.Flatten;
                if (shouldFlatten)
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

    /// <summary>
    /// 从流解析 Excel/CSV 时，MiniExcel 无法从“流”推断文件类型（原实现依赖路径扩展名）。
    /// 这里据可寻址性与 magic 字节判定：xlsx 本质为 zip（PK 头）按 Xlsx；否则按 Csv。
    /// 非可寻址流仅出现在 gzip/bzip2 包裹的 CSV（xlsx 内层总是已缓冲到可寻址内存流），故直接判 Csv。
    /// </summary>
    private static ExcelType ResolveExcelType(Stream stream)
    {
        if (!stream.CanSeek)
            return ExcelType.CSV;

        long pos = stream.Position;
        Span<byte> sig = stackalloc byte[2];
        bool isZip = stream.Read(sig) == 2 && sig[0] == (byte)'P' && sig[1] == (byte)'K';
        stream.Position = pos;
        return isZip ? ExcelType.XLSX : ExcelType.CSV;
    }

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
