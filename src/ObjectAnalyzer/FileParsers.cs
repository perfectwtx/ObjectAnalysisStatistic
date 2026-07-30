using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;
using MiniExcelLibs;
using YamlDotNet.Serialization;

namespace ObjectAnalyzer;

public static class FileParsers
{
    /// <summary>
    /// Checks that the file exists and is not empty.
    /// </summary>
    private static void ValidateFile(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"找不到输入文件: {path}");

        var info = new FileInfo(path);
        if (info.Length == 0)
            throw new InvalidDataException($"输入文件为空: {path}");
    }

    /// <summary>
    /// Parses a JSON file which can contain an array of objects or a single object.
    /// </summary>
    public static IEnumerable<Dictionary<string, object?>> ParseJson(string path, bool flatten)
    {
        ValidateFile(path);

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
        ValidateFile(path);

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
        ValidateFile(path);

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

    /// <summary>
    /// Parses an XML file. The root element contains multiple child elements,
    /// each representing an object. Each sub-element is a property.
    /// Example:
    /// <root>
    ///   <item><Id>1</Id><Name>张三</Name></item>
    ///   <item><Id>2</Id><Name>李四</Name></item>
    /// </root>
    /// </summary>
    public static IEnumerable<Dictionary<string, object?>> ParseXml(string path)
    {
        ValidateFile(path);

        XDocument doc;
        try
        {
            doc = XDocument.Load(path);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"解析 XML 文件时出错: {ex.Message}", ex);
        }

        if (doc.Root == null)
            throw new InvalidDataException("XML 文件缺少根节点。");

        // 每个子元素代表一个对象
        foreach (var element in doc.Root.Elements())
        {
            yield return ConvertXElement(element);
        }
    }

    /// <summary>
    /// Parses a YAML file containing a list of objects or a single object.
    /// </summary>
    public static IEnumerable<Dictionary<string, object?>> ParseYaml(string path)
    {
        ValidateFile(path);

        var deserializer = new DeserializerBuilder().Build();
        string yamlContent = File.ReadAllText(path);
        object? result = deserializer.Deserialize(yamlContent);

        if (result is List<object?> list)
        {
            foreach (var item in list)
            {
                if (item is Dictionary<object, object> dict)
                {
                    yield return ConvertYamlDict(dict);
                }
            }
        }
        else if (result is Dictionary<object, object> singleDict)
        {
            yield return ConvertYamlDict(singleDict);
        }
        else
        {
            throw new InvalidDataException("YAML 根节点必须是对象列表或单个对象。");
        }
    }

    /// <summary>
    /// Parses an Excel file where a specific column contains JSON strings.
    /// Each cell in the column is a complete JSON object text.
    /// </summary>
    /// <param name="path">Excel file path</param>
    /// <param name="columnName">Column name (e.g., "A", "B", "C"). Default is "A".</param>
    /// <param name="flatten">Whether to flatten nested JSON properties</param>
    public static IEnumerable<Dictionary<string, object?>> ParseExcelJsonColumn(string path, string columnName = "A", bool flatten = true)
    {
        ValidateFile(path);

        columnName = columnName.ToUpperInvariant();

        // 不使用表头模式读取原始数据，这样可以通过列字母访问
        var rows = MiniExcel.Query(path, useHeaderRow: false);
        int rowIndex = 0;

        foreach (var row in rows)
        {
            rowIndex++;

            if (row is not IDictionary<string, object?> rowDict)
                continue;

            // 获取指定列的值
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
                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidDataException($"第 {rowIndex} 行 (列 {columnName}) 的 JSON 根节点必须是一个对象。");
                }
                yield return ParseJsonElement(root, flatten);
            }
        }
    }

    private static Dictionary<string, object?> ConvertXElement(XElement element)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var child in element.Elements())
        {
            string key = child.Name.LocalName;
            // 如果子元素还有子元素，递归转换
            if (child.HasElements)
            {
                dict[key] = ConvertXElement(child);
            }
            else
            {
                dict[key] = child.Value;
            }
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
