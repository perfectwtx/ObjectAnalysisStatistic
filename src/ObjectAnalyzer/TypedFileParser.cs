using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace ObjectAnalyzer;

public static class TypedFileParser
{
    /// <summary>
    /// 使用指定的强类型反序列化 JSON 文件，并将结果转换为 Dictionary 列表。
    /// </summary>
    /// <param name="jsonPath">JSON 文件路径</param>
    /// <param name="targetType">目标类型（编译后的 class）</param>
    /// <returns>属性字典列表，供分析管线使用</returns>
    public static IEnumerable<Dictionary<string, object?>> ParseJson(string jsonPath, Type targetType)
    {
        if (!File.Exists(jsonPath))
            throw new FileNotFoundException($"找不到输入文件: {jsonPath}");

        string jsonContent = File.ReadAllText(jsonPath);

        // 构建数组类型: targetType[]
        Type arrayType = targetType.MakeArrayType();

        // 反序列化为对象数组
        object? deserialized = JsonSerializer.Deserialize(jsonContent, arrayType, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (deserialized is not System.Collections.IEnumerable enumerable)
            throw new InvalidOperationException("反序列化结果不是可枚举类型。JSON 根节点必须是数组。");

        // 获取目标类型的所有公共属性
        var properties = targetType.GetProperties(BindingFlags.Public | BindingFlags.Instance);

        foreach (var item in enumerable)
        {
            if (item == null) continue;

            var dict = new Dictionary<string, object?>();
            foreach (var prop in properties)
            {
                try
                {
                    dict[prop.Name] = prop.GetValue(item);
                }
                catch (Exception ex)
                {
                    dict[prop.Name] = $"<读取失败: {ex.Message}>";
                }
            }
            yield return dict;
        }
    }
}
