using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;
using MiniExcelLibs;

namespace ObjectAnalyzer.Parsing.Parsers;

/// <summary>
/// 解析器共享的值转换与扁平化助手：JSON / XML / YAML 到 Dictionary 的转换、
/// 对象图扁平化，以及 Excel 流类型判定。由各 IFileParser 实现共用。
/// </summary>
internal static class ParserHelpers
{

/// <summary>
/// 从流解析 Excel/CSV 时，MiniExcel 无法从“流”推断文件类型（原实现依赖路径扩展名）。
/// 这里据可寻址性与 magic 字节判定：xlsx 本质为 zip（PK 头）按 Xlsx；否则按 Csv。
/// 非可寻址流仅出现在 gzip/bzip2 包裹的 CSV（xlsx 内层总是已缓冲到可寻址内存流），故直接判 Csv。
/// </summary>
internal static ExcelType ResolveExcelType(Stream stream)
{
    if (!stream.CanSeek)
        return ExcelType.CSV;

    long pos = stream.Position;
    Span<byte> sig = stackalloc byte[2];
    bool isZip = stream.Read(sig) == 2 && sig[0] == (byte)'P' && sig[1] == (byte)'K';
    stream.Position = pos;
    return isZip ? ExcelType.XLSX : ExcelType.CSV;
}

internal static Dictionary<string, object?> ParseJsonElement(JsonElement element, bool flatten)
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

internal static object? ConvertJsonElement(JsonElement element)
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

internal static Dictionary<string, object?> ConvertXElement(XElement element, int depth)
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

internal static Dictionary<string, object?> ConvertYamlDict(Dictionary<object, object> yamlDict, int depth)
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

internal static object? ConvertYamlValue(object? value, int depth)
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
internal static Dictionary<string, object?> FlattenObjectDict(Dictionary<string, object?> source)
{
    var result = new Dictionary<string, object?>();
    FlattenObjectDictInto(source, "", result);
    return result;
}

internal static void FlattenObjectDictInto(
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
