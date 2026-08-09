using System;
using System.Buffers;
using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ObjectAnalyzer;

public static partial class ObjectAnalyzer
{
    private const int MaxTrackedUniqueValues = 1000;
    private const int ChunkSize = 10000;
    private const int MaxStoredDuplicateJsons = 10000;

    // 常用语义识别正则表达式（预编译提升性能）
    private static readonly Regex RegexDateTime = new(
        @"^\d{4}-\d{2}-\d{2}(T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})?)?$", RegexOptions.Compiled);
    private static readonly Regex RegexUuid = new(
        @"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", RegexOptions.Compiled);
    private static readonly Regex RegexEmail = new(
        @"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);
    private static readonly Regex RegexIPv4 = new(
        @"^(\d{1,3}\.){3}\d{1,3}$", RegexOptions.Compiled);
    private static readonly Regex RegexUrl = new(
        @"^(https?|ftp)://[^\s/$.?#].[^\s]*$", RegexOptions.Compiled);

    // 内部使用的线程局部扩展指标集
    private sealed class InternalFieldMetrics
    {
        public double MinNumber { get; set; } = double.MaxValue;
        public double MaxNumber { get; set; } = double.MinValue;
        public double SumNumber { get; set; }
        public int NumberCount { get; set; }

        public int MinLength { get; set; } = int.MaxValue;
        public int MaxLength { get; set; } = int.MinValue;
        public int StringCount { get; set; }

        public Dictionary<string, int> SemanticCounts { get; } = new();
    }

    private sealed class ThreadLocalState
    {
        public Dictionary<string, FieldStatistic> StatsMap { get; } = new();
        public Dictionary<string, InternalFieldMetrics> MetricsMap { get; } = new();
        public long TotalObjects { get; set; }
        public ArrayBufferWriter<byte> CanonicalWriter { get; } = new();
    }

    public static AnalysisResult Analyze(
        IEnumerable<Dictionary<string, object?>> objects,
        AnalysisRules? rules = null,
        ParallelOptions? parallelOptions = null,
        CancellationToken cancellationToken = default)
    {
        var globalSeenHashes = new ConcurrentDictionary<Hash128, byte>();
        var globalDuplicateJsons = new ConcurrentDictionary<Hash128, string>();

        var globalStatsMap = new Dictionary<string, FieldStatistic>();
        var globalMetricsMap = new Dictionary<string, InternalFieldMetrics>();
        var globalDuplicateCounts = new ConcurrentDictionary<Hash128, long>();
        long globalTotalObjects = 0;
        var mergeLock = new object();

        var options = parallelOptions ?? new ParallelOptions();

        void MergeLocalState(ThreadLocalState local)
        {
            lock (mergeLock)
            {
                globalTotalObjects += local.TotalObjects;

                foreach (var localStat in local.StatsMap.Values)
                {
                    string fName = localStat.FieldName;
                    if (!globalStatsMap.TryGetValue(fName, out var globalStat))
                    {
                        globalStat = new FieldStatistic { FieldName = fName };
                        globalStatsMap[fName] = globalStat;
                    }

                    globalStat.Count += localStat.Count;
                    globalStat.ValueCountsTruncated |= localStat.ValueCountsTruncated;

                    foreach (var tc in localStat.TypeCounts)
                    {
                        globalStat.TypeCounts[tc.Key] = globalStat.TypeCounts.GetValueOrDefault(tc.Key) + tc.Value;
                    }

                    foreach (var vc in localStat.ValueCounts)
                    {
                        if (ShouldTruncate(globalStat, vc.Key))
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

                // 合并数值与文本扩展指标
                foreach (var kvp in local.MetricsMap)
                {
                    string fName = kvp.Key;
                    var localMetrics = kvp.Value;

                    if (!globalMetricsMap.TryGetValue(fName, out var globalMetrics))
                    {
                        globalMetrics = new InternalFieldMetrics();
                        globalMetricsMap[fName] = globalMetrics;
                    }

                    if (localMetrics.NumberCount > 0)
                    {
                        globalMetrics.MinNumber = Math.Min(globalMetrics.MinNumber, localMetrics.MinNumber);
                        globalMetrics.MaxNumber = Math.Max(globalMetrics.MaxNumber, localMetrics.MaxNumber);
                        globalMetrics.SumNumber += localMetrics.SumNumber;
                        globalMetrics.NumberCount += localMetrics.NumberCount;
                    }

                    if (localMetrics.StringCount > 0)
                    {
                        globalMetrics.MinLength = Math.Min(globalMetrics.MinLength, localMetrics.MinLength);
                        globalMetrics.MaxLength = Math.Max(globalMetrics.MaxLength, localMetrics.MaxLength);
                        globalMetrics.StringCount += localMetrics.StringCount;

                        foreach (var sc in localMetrics.SemanticCounts)
                        {
                            globalMetrics.SemanticCounts[sc.Key] = globalMetrics.SemanticCounts.GetValueOrDefault(sc.Key) + sc.Value;
                        }
                    }
                }
            }
        }

        void ProcessChunk(IEnumerable<Dictionary<string, object?>> chunk)
        {
            Parallel.ForEach(
                chunk,
                options,
                () => new ThreadLocalState(),
                (obj, loopState, localState) =>
                {
                    localState.TotalObjects++;

                    // Fix #9: Array.Sort avoids LINQ lambda allocations in the per-object hot path
                    var sortedEntries = obj.ToArray();
                    Array.Sort(sortedEntries, static (a, b) => string.Compare(a.Key, b.Key, StringComparison.Ordinal));

                    // 将字段写入线程局部可复用字节缓冲区，避免为每个对象分配规范化字符串/byte[]
                    var writer = localState.CanonicalWriter;
                    writer.Clear();
                    foreach (var kv in sortedEntries)
                    {
                        string fieldName = kv.Key;
                        object? value = kv.Value;
                        string valStr = FormatValue(value);

                        WriteAsciiNumber(writer, fieldName.Length);
                        WriteByte(writer, (byte)':');
                        WriteUtf8(writer, fieldName);
                        WriteByte(writer, (byte)'=');
                        WriteAsciiNumber(writer, valStr.Length);
                        WriteByte(writer, (byte)':');
                        WriteUtf8(writer, valStr);

                        if (!localState.StatsMap.TryGetValue(fieldName, out var stat))
                        {
                            stat = new FieldStatistic { FieldName = fieldName };
                            localState.StatsMap[fieldName] = stat;
                        }

                        if (!localState.MetricsMap.TryGetValue(fieldName, out var metrics))
                        {
                            metrics = new InternalFieldMetrics();
                            localState.MetricsMap[fieldName] = metrics;
                        }

                        stat.Count++;

                        string valType = GetValueType(value);
                        stat.TypeCounts[valType] = stat.TypeCounts.GetValueOrDefault(valType) + 1;

                        if (ShouldTruncate(stat, valStr))
                        {
                            stat.ValueCountsTruncated = true;
                            stat.ValueCounts["[Other values]"] = stat.ValueCounts.GetValueOrDefault("[Other values]") + 1;
                        }
                        else
                        {
                            stat.ValueCounts[valStr] = stat.ValueCounts.GetValueOrDefault(valStr) + 1;
                        }

                        // 收集数值与字符串度量指标
                        RecordValueMetrics(value, valStr, metrics);
                    }

                    // 128 位非密码学哈希去重（替代慢速 MD5 + 十六进制字符串）
                    var objHash = ComputeHash128(writer.WrittenSpan);

                    if (!globalSeenHashes.TryAdd(objHash, 0))
                    {
                        // 仅记录实际重复的哈希：首次出现由 TryAdd 占用，故从 2 起计数
                        globalDuplicateCounts.AddOrUpdate(objHash, 2, (_, count) => count + 1);

                        if (globalDuplicateJsons.Count < MaxStoredDuplicateJsons)
                        {
                            globalDuplicateJsons.TryAdd(objHash, SerializeDuplicate(sortedEntries));
                        }
                    }

                    return localState;
                },
                MergeLocalState);
        }

        if (objects is IList<Dictionary<string, object?>> objectList)
        {
            // Fix #3: Use O(1) index-based slice instead of O(n) Skip/Take per chunk
            for (int offset = 0; offset < objectList.Count; offset += ChunkSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = Math.Min(ChunkSize, objectList.Count - offset);
                ProcessChunk(IndexedSlice(objectList, offset, count));
            }
        }
        else
        {
            foreach (var chunk in EnumerateChunks(objects, ChunkSize))
            {
                cancellationToken.ThrowIfCancellationRequested();
                ProcessChunk(chunk);
            }
        }

        // 后置处理：合并极值、推断默认值与语义分类
        var results = new List<FieldStatistic>();
        foreach (var stat in globalStatsMap.Values)
        {
            stat.Coverage = globalTotalObjects > 0 ? (double)stat.Count / globalTotalObjects : 0;

            stat.NullAppeared = stat.TypeCounts.ContainsKey("Null");
            stat.PrimaryType = stat.TypeCounts.OrderByDescending(t => t.Value).FirstOrDefault().Key ?? "String";

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

            // 补充合并数值与字符串扩展指标
            if (globalMetricsMap.TryGetValue(stat.FieldName, out var metrics))
            {
                if (metrics.NumberCount > 0)
                {
                    stat.MinNumber = metrics.MinNumber;
                    stat.MaxNumber = metrics.MaxNumber;
                    stat.SumNumber = metrics.SumNumber;
                    stat.AvgNumber = metrics.SumNumber / metrics.NumberCount;
                }

                if (metrics.StringCount > 0)
                {
                    stat.MinLength = metrics.MinLength;
                    stat.MaxLength = metrics.MaxLength;

                    // 当某语义匹配数量达到字符串总数的 80% 以上时，标注其 SemanticType
                    var topSemantic = metrics.SemanticCounts
                        .OrderByDescending(kv => kv.Value)
                        .FirstOrDefault();

                    if (topSemantic.Value > 0 && (double)topSemantic.Value / metrics.StringCount >= 0.8)
                    {
                        stat.SemanticType = topSemantic.Key;
                    }
                }
            }

            // 应用配置规则
            if (rules != null && rules.Fields.TryGetValue(stat.FieldName, out var rule))
            {
                stat.AppliedRule = rule;

                if (rule.DefaultValue != null)
                {
                    stat.DefaultValueRepresentation = rule.DefaultValue.ToString();
                }

                if (!string.IsNullOrEmpty(rule.PrimaryType))
                {
                    stat.PrimaryType = rule.PrimaryType;
                }

                if (rule.EnumValues != null && rule.EnumValues.Count > 0)
                {
                    ValidateEnumValues(stat, rule.EnumValues);
                }
            }

            results.Add(stat);
        }

        results.Sort((a, b) =>
        {
            int cmp = b.Count.CompareTo(a.Count);
            if (cmp == 0)
            {
                cmp = string.Compare(a.FieldName, b.FieldName, StringComparison.OrdinalIgnoreCase);
            }
            return cmp;
        });

        var duplicates = globalDuplicateCounts
            .Where(kv => kv.Value > 1 && globalDuplicateJsons.ContainsKey(kv.Key))
            .Select(kv => new DuplicateObjectStatistic
            {
                ObjectJson = globalDuplicateJsons[kv.Key],
                Count = kv.Value
            })
            .OrderByDescending(d => d.Count)
            .ToList();

        long totalUniqueObjects = globalSeenHashes.Count;
        long totalDuplicateGroups = globalDuplicateCounts.Count(kv => kv.Value > 1);

        return new AnalysisResult
        {
            TotalObjects = globalTotalObjects,
            TotalUniqueObjects = totalUniqueObjects,
            TotalDuplicateGroups = totalDuplicateGroups,
            DuplicateObjects = duplicates,
            FieldStatistics = results
        };
    }

    private static void RecordValueMetrics(object? value, string valStr, InternalFieldMetrics metrics)
    {
        if (value == null) return;

        // 1. 数值统计
        if (TryGetDoubleValue(value, out double dVal))
        {
            metrics.MinNumber = Math.Min(metrics.MinNumber, dVal);
            metrics.MaxNumber = Math.Max(metrics.MaxNumber, dVal);
            metrics.SumNumber += dVal;
            metrics.NumberCount++;
        }

        // 2. 字符串度量与语义测试
        if (value is string strVal)
        {
            metrics.MinLength = Math.Min(metrics.MinLength, strVal.Length);
            metrics.MaxLength = Math.Max(metrics.MaxLength, strVal.Length);
            metrics.StringCount++;

            if (RegexUuid.IsMatch(strVal))
                metrics.SemanticCounts["UUID"] = metrics.SemanticCounts.GetValueOrDefault("UUID") + 1;
            else if (RegexEmail.IsMatch(strVal))
                metrics.SemanticCounts["Email"] = metrics.SemanticCounts.GetValueOrDefault("Email") + 1;
            else if (RegexDateTime.IsMatch(strVal))
                metrics.SemanticCounts["DateTime"] = metrics.SemanticCounts.GetValueOrDefault("DateTime") + 1;
            else if (RegexIPv4.IsMatch(strVal))
                metrics.SemanticCounts["IPv4"] = metrics.SemanticCounts.GetValueOrDefault("IPv4") + 1;
            else if (RegexUrl.IsMatch(strVal))
                metrics.SemanticCounts["URL"] = metrics.SemanticCounts.GetValueOrDefault("URL") + 1;
        }
    }

    private static bool TryGetDoubleValue(object val, out double result)
    {
        if (val is double d) { result = d; return true; }
        if (val is float f) { result = f; return true; }
        if (val is int i) { result = i; return true; }
        if (val is long l) { result = l; return true; }
        if (val is decimal dec) { result = (double)dec; return true; }
        if (val is byte b) { result = b; return true; }
        if (val is short s) { result = s; return true; }
        if (val is uint ui) { result = ui; return true; }
        if (val is ulong ul) { result = ul; return true; }

        result = 0;
        return false;
    }

    /// <summary>
    /// 128 位紧凑哈希键（两个 ulong），替代 32 字符十六进制字符串键，减少字典内存占用。
    /// </summary>
    private readonly struct Hash128 : IEquatable<Hash128>
    {
        public readonly ulong High;
        public readonly ulong Low;

        public Hash128(ulong high, ulong low)
        {
            High = high;
            Low = low;
        }

        public bool Equals(Hash128 other) => High == other.High && Low == other.Low;

        public override bool Equals(object? obj) => obj is Hash128 other && Equals(other);

        public override int GetHashCode() => unchecked((int)High ^ (int)(High >> 32) ^ (int)Low ^ (int)(Low >> 32));
    }

    /// <summary>
    /// 128 位非密码学哈希：两条不同种子的 FNV-1a 64 通道（第二条按字节逆序）加 MurmurHash3 雪崩混合。
    /// 仅用于整行去重，128 位空间足够抵抗碰撞，且远快于 MD5。
    /// </summary>
    private static Hash128 ComputeHash128(ReadOnlySpan<byte> data)
    {
        ulong h1 = 14695981039346656037UL;
        ulong h2 = 0x84222325cbf29ce4UL;
        const ulong prime = 0x100000001b3UL;

        foreach (byte b in data)
        {
            h1 ^= b;
            h1 *= prime;
        }
        for (int i = data.Length - 1; i >= 0; i--)
        {
            h2 ^= data[i];
            h2 *= prime;
        }

        // 雪崩混合，降低低位相关性
        h1 ^= h1 >> 33;
        h1 *= 0xff51afd7ed558ccdUL;
        h1 ^= h1 >> 33;
        h2 ^= h2 >> 33;
        h2 *= 0xc4ceb9fe1a85ec53UL;
        h2 ^= h2 >> 33;

        return new Hash128(h1, h2);
    }

    private static void WriteUtf8(ArrayBufferWriter<byte> writer, string value)
    {
        int maxBytes = Encoding.UTF8.GetMaxByteCount(value.Length);
        Span<byte> span = writer.GetSpan(maxBytes);
        int written = Encoding.UTF8.GetBytes(value.AsSpan(), span);
        writer.Advance(written);
    }

    private static void WriteAsciiNumber(ArrayBufferWriter<byte> writer, int value)
    {
        Span<byte> span = writer.GetSpan(16);
        Utf8Formatter.TryFormat(value, span, out int written);
        writer.Advance(written);
    }

    private static void WriteByte(ArrayBufferWriter<byte> writer, byte value)
    {
        Span<byte> span = writer.GetSpan(1);
        span[0] = value;
        writer.Advance(1);
    }

    private static string SerializeDuplicate(KeyValuePair<string, object?>[] sortedEntries)
    {
        try
        {
            var sortedDict = new Dictionary<string, object?>(sortedEntries.Length, StringComparer.Ordinal);
            foreach (var kv in sortedEntries)
            {
                sortedDict[kv.Key] = kv.Value;
            }
            return JsonSerializer.Serialize(sortedDict, JsonSharedOptions.Write);
        }
        catch
        {
            var fallback = new Dictionary<string, string>(sortedEntries.Length);
            foreach (var kv in sortedEntries)
            {
                fallback[kv.Key] = SafeToString(kv.Value);
            }
            return JsonSerializer.Serialize(fallback, JsonSharedOptions.Write);
        }
    }

    // Fix #3: O(1) index-based slice over IList, avoids re-enumerating from offset
    private static IEnumerable<Dictionary<string, object?>> IndexedSlice(
        IList<Dictionary<string, object?>> list, int offset, int count)
    {
        int end = offset + count;
        for (int i = offset; i < end; i++)
            yield return list[i];
    }

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
                // 热路径使用紧凑序列化：更短、更快，且 ValueCounts 键不含换行
                return JsonSerializer.Serialize(value, JsonSharedOptions.WriteCompact);
            }
            catch
            {
                return value.ToString() ?? "";
            }
        }
        
        return value.ToString() ?? "";
    }

    private static string SafeToString(object? value)
    {
        if (value == null) return "null";
        if (value is string s) return s;
        try { return value.ToString() ?? ""; }
        catch { return "[unserializable]"; }
    }

    private static bool ShouldTruncate(FieldStatistic stat, string value)
    {
        if (stat.ValueCounts.ContainsKey(value)) return false;
        int tracked = stat.ValueCounts.Count -
            (stat.ValueCounts.ContainsKey("[Other values]") ? 1 : 0);
        return tracked >= MaxTrackedUniqueValues;
    }

    private static void ValidateEnumValues(FieldStatistic stat, List<System.Text.Json.JsonElement> enumElements)
    {
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

        foreach (var actualValue in stat.ValueCounts.Keys)
        {
            if (actualValue == "[Other values]") continue;

            if (IsEnumMatch(actualValue, enumStrings, enumNumbers))
                continue;

            stat.AddEnumViolation(actualValue); // Fix #8: use internal encapsulated method
        }
    }

    private static bool IsEnumMatch(string value, HashSet<string> enumStrings, HashSet<double> enumNumbers)
    {
        if (enumStrings.Contains(value))
            return true;

        if (double.TryParse(value, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out double actualNum))
        {
            if (enumNumbers.Contains(actualNum))
                return true;
        }

        return false;
    }
}
