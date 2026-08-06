using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace ObjectAnalyzer;

public static class ObjectAnalyzer
{
    private const int MaxTrackedUniqueValues = 1000;

    // 并行处理的分块大小：控制惰性输入的内存占用上限（每块对象 + 线程局部状态）
    private const int ChunkSize = 10000;

    // 内部使用的线程局部状态类
    private sealed class ThreadLocalState
    {
        public Dictionary<string, FieldStatistic> StatsMap { get; } = new();
        public Dictionary<string, int> DuplicateCounts { get; } = new();
        public int TotalObjects { get; set; }
    }

    /// <summary>
    /// Analyzes a collection of objects, producing field statistics and duplicate detection.
    /// </summary>
    /// <param name="objects">Objects to analyze.</param>
    /// <param name="rules">Optional field rules (defaults, primary types, enum constraints).</param>
    /// <param name="parallelOptions">Optional parallelism control (MaxDegreeOfParallelism / CancellationToken).
    /// If null, uses the default (all cores, no cancellation).</param>
    public static AnalysisResult Analyze(
        IEnumerable<Dictionary<string, object?>> objects,
        AnalysisRules? rules = null,
        ParallelOptions? parallelOptions = null)
    {
        var globalSeenHashes = new ConcurrentDictionary<string, byte>();
        var globalDuplicateJsons = new ConcurrentDictionary<string, string>();

        var globalStatsMap = new Dictionary<string, FieldStatistic>();
        var globalDuplicateCounts = new Dictionary<string, int>();
        int globalTotalObjects = 0;
        var mergeLock = new object();

        var options = parallelOptions ?? new ParallelOptions();

        // Reduce：将线程局部的状态合并入全局状态（多个并行线程并发调用，需加锁）
        void MergeLocalState(ThreadLocalState local)
        {
            lock (mergeLock)
            {
                globalTotalObjects += local.TotalObjects;

                foreach (var localStat in local.StatsMap.Values)
                {
                    if (!globalStatsMap.TryGetValue(localStat.FieldName, out var globalStat))
                    {
                        globalStat = new FieldStatistic { FieldName = localStat.FieldName };
                        globalStatsMap[localStat.FieldName] = globalStat;
                    }

                    globalStat.Count += localStat.Count;
                    globalStat.ValueCountsTruncated |= localStat.ValueCountsTruncated;

                    foreach (var tc in localStat.TypeCounts)
                    {
                        globalStat.TypeCounts[tc.Key] = globalStat.TypeCounts.GetValueOrDefault(tc.Key) + tc.Value;
                    }

                    foreach (var vc in localStat.ValueCounts)
                    {
                        if (globalStat.ValueCounts.Count >= MaxTrackedUniqueValues && !globalStat.ValueCounts.ContainsKey(vc.Key))
                        {
                            globalStat.ValueCountsTruncated = true;
                            globalStat.ValueCounts["[Other values]"] = globalStat.ValueCounts.GetValueOrDefault("[Other values]") + vc.Value;
                        }
                        else
                        {
                            globalStat.ValueCounts[vc.Key] = globalStat.ValueCounts.GetValueOrDefault(vc.Key) + vc.Value;
                        }
                    }
                }

                foreach (var kvp in local.DuplicateCounts)
                {
                    globalDuplicateCounts[kvp.Key] = globalDuplicateCounts.GetValueOrDefault(kvp.Key) + kvp.Value;
                }
            }
        }

        // Map：对一块对象执行并行聚合（哈希构建与字段统计共享 FormatValue，嵌套对象只序列化一次）
        void ProcessChunk(IEnumerable<Dictionary<string, object?>> chunk)
        {
            Parallel.ForEach(
                chunk,
                options,
                () => new ThreadLocalState(),
                (obj, loopState, localState) =>
                {
                    localState.TotalObjects++;

                    // 排序仅执行一次，哈希构建与重复对象 JSON 序列化共享
                    var sortedEntries = obj.OrderBy(kv => kv.Key).ToArray();

                    // 单遍处理：构建规范化字符串（用于哈希）+ 字段统计，FormatValue 每属性只调用一次
                    var sb = new StringBuilder();
                    foreach (var kv in sortedEntries)
                    {
                        string fieldName = kv.Key;
                        object? value = kv.Value;
                        string valStr = FormatValue(value);

                        sb.Append(fieldName);
                        sb.Append('=');
                        sb.Append(valStr);
                        sb.Append('&');

                        if (!localState.StatsMap.TryGetValue(fieldName, out var stat))
                        {
                            stat = new FieldStatistic { FieldName = fieldName };
                            localState.StatsMap[fieldName] = stat;
                        }

                        stat.Count++;

                        string valType = GetValueType(value);
                        stat.TypeCounts[valType] = stat.TypeCounts.GetValueOrDefault(valType) + 1;

                        if (stat.ValueCounts.Count >= MaxTrackedUniqueValues && !stat.ValueCounts.ContainsKey(valStr))
                        {
                            stat.ValueCountsTruncated = true;
                            stat.ValueCounts["[Other values]"] = stat.ValueCounts.GetValueOrDefault("[Other values]") + 1;
                        }
                        else
                        {
                            stat.ValueCounts[valStr] = stat.ValueCounts.GetValueOrDefault(valStr) + 1;
                        }
                    }

                    // 对象哈希指纹（128 位 MD5，避免 64 位哈希的生日悖论碰撞）
                    string objHash = ComputeHash(sb.ToString());

                    // 检测重复对象：若哈希已见过则序列化保存实体（仅对重复对象执行）
                    if (!globalSeenHashes.TryAdd(objHash, 0))
                    {
                        globalDuplicateJsons.GetOrAdd(objHash, h =>
                        {
                            var sortedDict = sortedEntries.ToDictionary(kv => kv.Key, kv => kv.Value);
                            return JsonSerializer.Serialize(sortedDict, JsonSharedOptions.Write);
                        });
                    }
                    localState.DuplicateCounts[objHash] = localState.DuplicateCounts.GetValueOrDefault(objHash) + 1;

                    return localState;
                },
                MergeLocalState);
        }

        // 分块处理：IList 直接按索引切片，惰性 IEnumerable 按块流式读取，避免一次性物化全部对象
        if (objects is IList<Dictionary<string, object?>> objectList)
        {
            for (int offset = 0; offset < objectList.Count; offset += ChunkSize)
            {
                int count = Math.Min(ChunkSize, objectList.Count - offset);
                ProcessChunk(objectList.Skip(offset).Take(count));
            }
        }
        else
        {
            foreach (var chunk in EnumerateChunks(objects, ChunkSize))
            {
                ProcessChunk(chunk);
            }
        }

        // 后置处理：计算覆盖率并推断默认值
        var results = new List<FieldStatistic>();
        foreach (var stat in globalStatsMap.Values)
        {
            stat.Coverage = globalTotalObjects > 0 ? (double)stat.Count / globalTotalObjects : 0;

            // 1. 判断是否显式出现过 null（字段缺失不等于 null）
            stat.NullAppeared = stat.TypeCounts.ContainsKey("Null") || 
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

            // 4. 应用规则（覆盖自动推断结果）
            if (rules != null && rules.Fields.TryGetValue(stat.FieldName, out var rule))
            {
                stat.AppliedRule = rule;

                // 规则指定默认值 → 覆盖
                if (rule.DefaultValue != null)
                {
                    stat.DefaultValueRepresentation = rule.DefaultValue.ToString();
                }

                // 规则指定主导类型 → 覆盖
                if (!string.IsNullOrEmpty(rule.PrimaryType))
                {
                    stat.PrimaryType = rule.PrimaryType;
                }

                // 规则指定枚举约束 → 校验
                if (rule.EnumValues != null && rule.EnumValues.Count > 0)
                {
                    ValidateEnumValues(stat, rule.EnumValues);
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

    /// <summary>
    /// Computes a 128-bit hash of the object's canonical representation.
    /// Uses MD5 for speed (not for cryptographic security), avoiding birthday-paradox
    /// collisions that occur with 64-bit hashes at scale.
    /// </summary>
#pragma warning disable CA5351 // MD5 used for deduplication, not security
    private static string ComputeHash(string canonical) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(canonical)));
#pragma warning restore CA5351

    /// <summary>
    /// 将惰性输入按固定大小分块产出，控制流式分析时的内存占用。
    /// </summary>
    private static IEnumerable<List<Dictionary<string, object?>>> EnumerateChunks(
        IEnumerable<Dictionary<string, object?>> source, int chunkSize)
    {
        var chunk = new List<Dictionary<string, object?>>(chunkSize);
        foreach (var obj in source)
        {
            chunk.Add(obj);
            if (chunk.Count >= chunkSize)
            {
                yield return chunk;
                chunk = new List<Dictionary<string, object?>>(chunkSize);
            }
        }
        if (chunk.Count > 0)
            yield return chunk;
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
        
        if (value is IDictionary<string, object?> || (value is System.Collections.IEnumerable and not string))
        {
            try
            {
                return JsonSerializer.Serialize(value, JsonSharedOptions.Write);
            }
            catch
            {
                return value.ToString() ?? "";
            }
        }
        
        return value.ToString() ?? "";
    }

    /// <summary>
    /// 校验字段实际值是否符合枚举约束。
    /// 匹配逻辑：字符串精确匹配(忽略大小写) + 数值解析双模式。
    /// </summary>
    private static void ValidateEnumValues(FieldStatistic stat, List<System.Text.Json.JsonElement> enumElements)
    {
        // 将枚举值转换为字符串集合和数值集合
        var enumStrings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var enumNumbers = new HashSet<double>();

        foreach (var elem in enumElements)
        {
            switch (elem.ValueKind)
            {
                case JsonValueKind.String:
                    var strVal = elem.GetString();
                    if (strVal != null) enumStrings.Add(strVal);
                    break;
                case JsonValueKind.Number:
                    if (elem.TryGetDouble(out double numVal)) enumNumbers.Add(numVal);
                    break;
                default:
                    var raw = elem.GetRawText();
                    if (raw != null) enumStrings.Add(raw);
                    break;
            }
        }

        // 校验每个实际值
        foreach (var actualValue in stat.ValueCounts.Keys)
        {
            if (actualValue == "[Other values]") continue;

            if (IsEnumMatch(actualValue, enumStrings, enumNumbers))
                continue;

            stat.EnumViolations.Add(actualValue);
        }
    }

    private static bool IsEnumMatch(string value, HashSet<string> enumStrings, HashSet<double> enumNumbers)
    {
        // 1. 字符串精确匹配 (忽略大小写)
        if (enumStrings.Contains(value))
            return true;

        // 2. 数值匹配: 实际值和枚举数值都尝试解析为 double
        if (double.TryParse(value, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out double actualNum))
        {
            if (enumNumbers.Contains(actualNum))
                return true;
        }

        return false;
    }
}
