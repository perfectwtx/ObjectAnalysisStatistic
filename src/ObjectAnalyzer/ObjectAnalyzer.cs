using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace ObjectAnalyzer;

public static class ObjectAnalyzer
{
    private const int MaxTrackedUniqueValues = 1000;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    // 内部使用的线程局部状态类
    private sealed class ThreadLocalState
    {
        public Dictionary<string, FieldStatistic> StatsMap { get; } = new();
        public Dictionary<long, int> DuplicateCounts { get; } = new();
        public int TotalObjects { get; set; }
    }

    public static AnalysisResult Analyze(IEnumerable<Dictionary<string, object?>> objects)
    {
        var globalSeenHashes = new ConcurrentDictionary<long, byte>();
        var globalDuplicateJsons = new ConcurrentDictionary<long, string>();

        var globalStatsMap = new Dictionary<string, FieldStatistic>();
        var globalDuplicateCounts = new Dictionary<long, int>();
        int globalTotalObjects = 0;
        object mergeLock = new();

        // 使用 Parallel.ForEach 进行多线程并行 Map 聚合
        Parallel.ForEach(
            objects,
            () => new ThreadLocalState(),
            (obj, loopState, localState) =>
            {
                localState.TotalObjects++;

                // 1. 计算对象哈希指纹（不进行完整 JSON 序列化以节省内存）
                long objHash = ComputeObjectHash(obj);

                // 2. 检测重复对象
                // 若该哈希已被其他线程或本线程见过，则是重复对象，此时才进行 JSON 序列化保存其实体
                if (!globalSeenHashes.TryAdd(objHash, 0))
                {
                    globalDuplicateJsons.GetOrAdd(objHash, h =>
                    {
                        var sortedDict = obj.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key, kv => kv.Value);
                        return JsonSerializer.Serialize(sortedDict, SerializerOptions);
                    });
                }
                localState.DuplicateCounts[objHash] = localState.DuplicateCounts.GetValueOrDefault(objHash) + 1;

                // 3. 统计字段属性
                foreach (var kvp in obj)
                {
                    string fieldName = kvp.Key;
                    object? value = kvp.Value;

                    if (!localState.StatsMap.TryGetValue(fieldName, out var stat))
                    {
                        stat = new FieldStatistic { FieldName = fieldName };
                        localState.StatsMap[fieldName] = stat;
                    }

                    stat.Count++;

                    // 统计数据类型
                    string valType = GetValueType(value);
                    stat.TypeCounts[valType] = stat.TypeCounts.GetValueOrDefault(valType) + 1;

                    // 统计具体值频次 (包含内存修剪)
                    string valStr = FormatValue(value);
                    if (stat.ValueCounts.Count >= MaxTrackedUniqueValues && !stat.ValueCounts.ContainsKey(valStr))
                    {
                        stat.ValueCounts["[Other values]"] = stat.ValueCounts.GetValueOrDefault("[Other values]") + 1;
                    }
                    else
                    {
                        stat.ValueCounts[valStr] = stat.ValueCounts.GetValueOrDefault(valStr) + 1;
                    }
                }

                return localState;
            },
            localState =>
            {
                // Reduce 阶段：将线程局部的状态合并入全局状态
                lock (mergeLock)
                {
                    globalTotalObjects += localState.TotalObjects;

                    // 合并字段统计
                    foreach (var localStat in localState.StatsMap.Values)
                    {
                        if (!globalStatsMap.TryGetValue(localStat.FieldName, out var globalStat))
                        {
                            globalStat = new FieldStatistic { FieldName = localStat.FieldName };
                            globalStatsMap[localStat.FieldName] = globalStat;
                        }

                        globalStat.Count += localStat.Count;

                        foreach (var tc in localStat.TypeCounts)
                        {
                            globalStat.TypeCounts[tc.Key] = globalStat.TypeCounts.GetValueOrDefault(tc.Key) + tc.Value;
                        }

                        foreach (var vc in localStat.ValueCounts)
                        {
                            if (globalStat.ValueCounts.Count >= MaxTrackedUniqueValues && !globalStat.ValueCounts.ContainsKey(vc.Key))
                            {
                                globalStat.ValueCounts["[Other values]"] = globalStat.ValueCounts.GetValueOrDefault("[Other values]") + vc.Value;
                            }
                            else
                            {
                                globalStat.ValueCounts[vc.Key] = globalStat.ValueCounts.GetValueOrDefault(vc.Key) + vc.Value;
                            }
                        }
                    }

                    // 合并重复对象计数
                    foreach (var kvp in localState.DuplicateCounts)
                    {
                        globalDuplicateCounts[kvp.Key] = globalDuplicateCounts.GetValueOrDefault(kvp.Key) + kvp.Value;
                    }
                }
            }
        );

        // 后置处理：计算覆盖率并推断默认值
        var results = new List<FieldStatistic>();
        foreach (var stat in globalStatsMap.Values)
        {
            stat.Coverage = globalTotalObjects > 0 ? (double)stat.Count / globalTotalObjects : 0;

            // 1. 判断是否出现过 null (显式出现 null，或者字段在某些对象中缺失)
            stat.NullAppeared = (stat.Count < globalTotalObjects) || 
                                stat.TypeCounts.ContainsKey("Null") || 
                                stat.ValueCounts.ContainsKey("null");

            // 2. 确定该字段的主导数据类型
            stat.PrimaryType = stat.TypeCounts.OrderByDescending(t => t.Value).FirstOrDefault().Key ?? "String";

            // 3. 确定该字段的默认值表现形式
            if (stat.NullAppeared)
            {
                stat.DefaultValueRepresentation = "null";
            }
            else
            {
                if (stat.PrimaryType == "Number")
                {
                    stat.DefaultValueRepresentation = "0";
                }
                else if (stat.PrimaryType == "Boolean")
                {
                    stat.DefaultValueRepresentation = "false";
                }
                else
                {
                    stat.DefaultValueRepresentation = null;
                }
            }

            results.Add(stat);
        }

        // 排序结果
        results.Sort((a, b) =>
        {
            int cmp = b.Count.CompareTo(a.Count);
            if (cmp == 0)
            {
                cmp = string.Compare(a.FieldName, b.FieldName, StringComparison.OrdinalIgnoreCase);
            }
            return cmp;
        });

        // 收集重复对象实体
        var duplicates = globalDuplicateCounts
            .Where(kv => kv.Value > 1 && globalDuplicateJsons.ContainsKey(kv.Key))
            .Select(kv => new DuplicateObjectStatistic
            {
                ObjectJson = globalDuplicateJsons[kv.Key],
                Count = kv.Value
            })
            .OrderByDescending(d => d.Count)
            .ToList();

        // 唯一对象数量 = 已记录的总哈希数
        int totalUniqueObjects = globalSeenHashes.Count;

        return new AnalysisResult
        {
            TotalObjects = globalTotalObjects,
            TotalUniqueObjects = totalUniqueObjects,
            DuplicateObjects = duplicates,
            FieldStatistics = results
        };
    }

    private static long ComputeObjectHash(Dictionary<string, object?> obj)
    {
        // FNV-1a 64位哈希实现
        ulong hash = 14695981039346656037UL;

        // 排序键值，以确保不同对象但键值对相同的实体哈希一致
        var sorted = obj.OrderBy(kv => kv.Key);
        foreach (var kv in sorted)
        {
            // 哈希 Key
            foreach (char c in kv.Key)
            {
                hash ^= c;
                hash *= 1099511628211UL;
            }
            // 哈希 Value 字符串表现形式
            string valStr = FormatValue(kv.Value);
            foreach (char c in valStr)
            {
                hash ^= c;
                hash *= 1099511628211UL;
            }
        }
        return (long)hash;
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
