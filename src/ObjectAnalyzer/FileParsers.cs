using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using MiniExcelLibs;

namespace ObjectAnalyzer;

public static class FileParsers
{
    /// <summary>
    /// Parses a JSON file which can contain an array of objects or a single object.
    /// </summary>
    public static IEnumerable<Dictionary<string, object?>> ParseJson(string path, bool flatten)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"找不到输入文件: {path}");

        string jsonContent = File.ReadAllText(path);
        using JsonDocument doc = JsonDocument.Parse(jsonContent);
        JsonElement root = doc.RootElement;

        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement element in root.EnumerateArray())
            {
                yield return ParseJsonElement(element, flatten);
            }
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            yield return ParseJsonElement(root, flatten);
        }
        else
        {
            throw new InvalidDataException("JSON 根节点必须是数组或对象。");
        }
    }

    /// <summary>
    /// Parses a JSON Lines (NDJSON) file where each line is a JSON object.
    /// </summary>
    public static IEnumerable<Dictionary<string, object?>> ParseJsonl(string path, bool flatten)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"找不到输入文件: {path}");

        using var reader = new StreamReader(path);
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
                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidDataException($"第 {lineNumber} 行的 JSON 根节点必须是一个对象。");
                }
                yield return ParseJsonElement(root, flatten);
            }
        }
    }

    /// <summary>
    /// Parses an Excel file (.xlsx) or CSV file using MiniExcel.
    /// CSV is also supported by MiniExcel.
    /// </summary>
    public static IEnumerable<Dictionary<string, object?>> ParseExcelOrCsv(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"找不到输入文件: {path}");

        var rows = MiniExcel.Query(path, useHeaderRow: true);
        foreach (var row in rows)
        {
            var dict = new Dictionary<string, object?>();
            if (row is IDictionary<string, object?> rowDict)
            {
                foreach (var kvp in rowDict)
                {
                    if (kvp.Key != null)
                    {
                        dict[kvp.Key] = kvp.Value;
                    }
                }
            }
            yield return dict;
        }
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
                {
                    target[property.Name] = ConvertJsonElement(property.Value);
                }
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
                {
                    dict[property.Name] = ConvertJsonElement(property.Value);
                }
                return dict;

            case JsonValueKind.Array:
                var list = new List<object?>();
                foreach (var item in element.EnumerateArray())
                {
                    list.Add(ConvertJsonElement(item));
                }
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
}
