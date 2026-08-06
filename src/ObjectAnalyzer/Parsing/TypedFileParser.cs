using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace ObjectAnalyzer;

/// <summary>
/// 强类型反序列化的结果：对象列表 + 属性读取错误列表（分离，避免错误文本污染统计数据）。
/// </summary>
public sealed class TypedParseResult
{
    public List<Dictionary<string, object?>> Objects { get; } = new();
    public List<string> Errors { get; } = new();
}

public static class TypedFileParser
{
    /// <summary>
    /// 使用指定的强类型反序列化 JSON 文件，并将结果转换为 Dictionary 列表。
    /// 支持 JSON 根节点为数组或单个对象。
    /// </summary>
    /// <param name="jsonPath">JSON 文件路径</param>
    /// <param name="targetType">目标类型（编译后的 class）</param>
    /// <returns>属性字典列表与读取错误列表，供分析管线使用</returns>
    public static TypedParseResult ParseJson(string jsonPath, Type targetType)
    {
        if (!File.Exists(jsonPath))
            throw new FileNotFoundException($"找不到输入文件: {jsonPath}");

        var result = new TypedParseResult();

        // 获取目标类型的所有公共属性
        var properties = targetType.GetProperties(BindingFlags.Public | BindingFlags.Instance);

        // 先判断根节点类型：数组 → 反序列化为 targetType[]；单个对象 → 包装为单元素列表
        using (var fs = File.OpenRead(jsonPath))
        using (var doc = JsonDocument.Parse(fs))
        {
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                var jsonText = File.ReadAllText(jsonPath);
                var single = JsonSerializer.Deserialize(jsonText, targetType, JsonSharedOptions.Read);
                if (single != null)
                {
                    result.Objects.Add(BuildDict(single, properties, targetType, result));
                }
                return result;
            }
        }

        // 数组路径
        Type arrayType = targetType.MakeArrayType();
        object? deserialized;
        using (var fs = File.OpenRead(jsonPath))
        {
            deserialized = JsonSerializer.Deserialize(fs, arrayType, JsonSharedOptions.Read);
        }

        if (deserialized is not System.Collections.IEnumerable enumerable)
            throw new InvalidOperationException("反序列化结果不是可枚举类型。JSON 根节点必须是数组或对象。");

        foreach (var item in enumerable)
        {
            if (item == null) continue;
            result.Objects.Add(BuildDict(item, properties, targetType, result));
        }
        return result;
    }

    private static Dictionary<string, object?> BuildDict(
        object item, IEnumerable<PropertyInfo> properties, Type targetType, TypedParseResult result)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var prop in properties)
        {
            try
            {
                dict[prop.Name] = prop.GetValue(item);
            }
            catch (Exception ex)
            {
                // 读取失败不污染统计数据：跳过该属性，并单独记录错误
                result.Errors.Add($"{targetType.Name}.{prop.Name}: {ex.Message}");
            }
        }
        return dict;
    }
}
