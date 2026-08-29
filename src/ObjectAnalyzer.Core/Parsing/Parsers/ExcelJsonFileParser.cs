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

namespace ObjectAnalyzer.Parsing.Parsers;

internal sealed class ExcelJsonFileParser : IFileParser
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
        foreach (var row in MiniExcel.Query(stream, useHeaderRow: useHeaderRow, excelType: ParserHelpers.ResolveExcelType(stream)))
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
        foreach (var (rowIndex, row) in Indexed(MiniExcel.Query(stream, useHeaderRow: useHeaderRow, excelType: ParserHelpers.ResolveExcelType(stream))))
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
        foreach (var (rowIndex, row) in Indexed(MiniExcel.Query(stream, useHeaderRow: useHeaderRow, excelType: ParserHelpers.ResolveExcelType(stream))))
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
                ParserHelpers.FlattenObjectDictInto(nestedDict, sourceKey, result, 0);
                break;
            case IList list:
                for (int i = 0; i < list.Count; i++)
                {
                    string indexedKey = $"{sourceKey}[{i}]";
                    var item = list[i];
                    if (item is Dictionary<string, object?> itemDict)
                    {
                        ParserHelpers.FlattenObjectDictInto(itemDict, indexedKey, result, 0);
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
            JsonValueKind.Object => ParserHelpers.ParseJsonElement(element, true),
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
