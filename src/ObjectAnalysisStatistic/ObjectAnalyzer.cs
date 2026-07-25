using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ObjectAnalysisStatistic;

public class FieldStatistic
{
    public string FieldName { get; set; } = "";
    public int Count { get; set; }
    public double Coverage { get; set; } // 字段在所有对象中的覆盖率 (0.0 - 1.0)
    public Dictionary<string, int> TypeCounts { get; set; } = new();
    public Dictionary<string, int> ValueCounts { get; set; } = new();
    
    // 以下为关于默认值的属性
    public string PrimaryType { get; set; } = "Unknown";
    public bool NullAppeared { get; set; }
    public string? DefaultValueRepresentation { get; set; }
}

public class DuplicateObjectStatistic
{
    public string ObjectJson { get; set; } = "";
    public int Count { get; set; }
}

public class AnalysisResult
{
    public int TotalObjects { get; set; }
    public int TotalUniqueObjects { get; set; }
    public List<DuplicateObjectStatistic> DuplicateObjects { get; set; } = new();
    public List<FieldStatistic> FieldStatistics { get; set; } = new();
}

public static class ObjectAnalyzer
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static AnalysisResult Analyze(IEnumerable<Dictionary<string, object?>> objects)
    {
        // 先转换为列表，以便进行多重分析
        var objectList = objects.ToList();
        int totalObjects = objectList.Count;

        var statsMap = new Dictionary<string, FieldStatistic>();

        foreach (var obj in objectList)
        {
            foreach (var kvp in obj)
            {
                string fieldName = kvp.Key;
                object? value = kvp.Value;

                if (!statsMap.TryGetValue(fieldName, out var stat))
                {
                    stat = new FieldStatistic { FieldName = fieldName };
                    statsMap[fieldName] = stat;
                }

                stat.Count++;

                // 统计值的数据类型
                string valType = GetValueType(value);
                stat.TypeCounts[valType] = stat.TypeCounts.GetValueOrDefault(valType) + 1;

                // 统计具体的取值频次
                string valStr = FormatValue(value);
                stat.ValueCounts[valStr] = stat.ValueCounts.GetValueOrDefault(valStr) + 1;
            }
        }

        // 计算覆盖率、分析默认值并整理结果
        var results = new List<FieldStatistic>();
        foreach (var stat in statsMap.Values)
        {
            stat.Coverage = totalObjects > 0 ? (double)stat.Count / totalObjects : 0;

            // 1. 判断是否出现过 null (显式出现 null，或者字段在某些对象中缺失)
            stat.NullAppeared = (stat.Count < totalObjects) || 
                                stat.TypeCounts.ContainsKey("Null") || 
                                stat.ValueCounts.ContainsKey("null");

            // 2. 确定该字段的主导数据类型
            stat.PrimaryType = stat.TypeCounts.OrderByDescending(t => t.Value).FirstOrDefault().Key ?? "String";

            // 3. 确定该字段的默认值表现形式
            if (stat.NullAppeared)
            {
                // 如果值类型或引用类型出现过 null，则 null 即为默认值
                stat.DefaultValueRepresentation = "null";
            }
            else
            {
                // 如果没出现过 null
                if (stat.PrimaryType == "Number")
                {
                    stat.DefaultValueRepresentation = "0"; // 对应数值类型的 0
                }
                else if (stat.PrimaryType == "Boolean")
                {
                    stat.DefaultValueRepresentation = "false"; // 对应 bool 类型的 false
                }
                else
                {
                    // 引用类型如未出现 null，则在数据集中暂时没有观察到默认值
                    stat.DefaultValueRepresentation = null;
                }
            }

            results.Add(stat);
        }

        // 默认按出现次数降序排列，如果次数相同则按字段名升序排列
        results.Sort((a, b) =>
        {
            int cmp = b.Count.CompareTo(a.Count);
            if (cmp == 0)
            {
                cmp = string.Compare(a.FieldName, b.FieldName, StringComparison.OrdinalIgnoreCase);
            }
            return cmp;
        });

        // 统计重复对象
        var objCounts = new Dictionary<string, int>();
        foreach (var obj in objectList)
        {
            // 排序键以确保相同属性集的对象序列化结果一致
            var sortedDict = obj.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key, kv => kv.Value);
            string serialized = JsonSerializer.Serialize(sortedDict, SerializerOptions);
            objCounts[serialized] = objCounts.GetValueOrDefault(serialized) + 1;
        }

        var duplicates = objCounts
            .Where(kv => kv.Value > 1)
            .Select(kv => new DuplicateObjectStatistic { ObjectJson = kv.Key, Count = kv.Value })
            .OrderByDescending(d => d.Count)
            .ToList();

        return new AnalysisResult
        {
            TotalObjects = totalObjects,
            TotalUniqueObjects = objCounts.Count,
            DuplicateObjects = duplicates,
            FieldStatistics = results
        };
    }

    private static string GetValueType(object? value)
    {
        if (value == null) return "Null";
        if (value is bool) return "Boolean";
        
        if (value is sbyte || value is byte || value is short || value is ushort || 
            value is int || value is uint || value is long || value is ulong || 
            value is float || value is double || value is decimal)
        {
            return "Number";
        }
        
        if (value is string) return "String";
        if (value is IDictionary<string, object?> || value is System.Dynamic.ExpandoObject) return "Object";
        if (value is System.Collections.IEnumerable) return "Array";
        
        return value.GetType().Name;
    }

    private static string FormatValue(object? value)
    {
        if (value == null) return "null";
        if (value is string s) return s;
        
        // 复杂类型（如嵌套的 Dictionary 或 List），序列化为 JSON 字符串显示
        if (value is IDictionary<string, object?> || (value is System.Collections.IEnumerable && !(value is string)))
        {
            try
            {
                return JsonSerializer.Serialize(value, SerializerOptions);
            }
            catch
            {
                return value.ToString() ?? "";
            }
        }
        
        return value.ToString() ?? "";
    }
}
