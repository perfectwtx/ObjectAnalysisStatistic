using System.Buffers;
using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using ObjectAnalyzer.Analysis.Options;

namespace ObjectAnalyzer.Analysis;

public static partial class ObjectAnalyzer
{
    private const int MaxTrackedUniqueValues = 1000;
    private const int ChunkSize = 10000;

    /// <summary>Typed entry point. Literal property names and structural paths remain unambiguous.</summary>
    public static AnalysisResult AnalyzeRecords(
        IEnumerable<IReadOnlyDictionary<FieldPath, RecordValue>> records,
        AnalysisRules? rules = null,
        ParallelOptions? parallelOptions = null,
        AdvancedAnalysisOptions? features = null,
        IProgress<long>? progress = null,
        int? maxTrackedValues = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        return Analyze(records.Select(record => record.ToDictionary(
            p => p.Key.ToString(), p => (object?)p.Value, StringComparer.Ordinal)),
            rules, parallelOptions, features, progress, maxTrackedValues, cancellationToken);
    }

    /// <summary>
    /// 主流分析入口：返回 <see cref="AnalysisResult"/>，不暴露内部草图。
    /// 需要每字段的 t-digest / HyperLogLog 草图（用于分布快照 / 漂移）时使用 <see cref="AnalyzeDetailed"/>。
    /// </summary>
    public static AnalysisResult Analyze(
        IEnumerable<Dictionary<string, object?>> objects,
        AnalysisRules? rules = null,
        ParallelOptions? parallelOptions = null,
        AdvancedAnalysisOptions? features = null,
        IProgress<long>? progress = null,
        int? maxTrackedValues = null,
        CancellationToken cancellationToken = default)
    {
        return AnalyzeCore(objects, rules, parallelOptions, features, cancellationToken, progress, maxTrackedValues).Result;
    }

    /// <summary>
    /// 深度分析 增强入口：在返回 <see cref="AnalysisResult"/> 的同时，暴露每字段的 t-digest 草图与 HyperLogLog 草图，
    /// 供 <c>--export-distribution-snapshot</c> / <c>--diff-distribution</c> 做分布快照与漂移检测。
    /// 调用方无需草图时使用 <see cref="Analyze"/> 即可。
    /// </summary>
    public static (
        AnalysisResult Result,
        Dictionary<string, MergeableQuantileSketch>? QuantileSketches,
        Dictionary<string, HyperLogLog>? DistinctSketches
    ) AnalyzeDetailed(
        IEnumerable<Dictionary<string, object?>> objects,
        AnalysisRules? rules = null,
        ParallelOptions? parallelOptions = null,
        AdvancedAnalysisOptions? features = null,
        IProgress<long>? progress = null,
        int? maxTrackedValues = null,
        CancellationToken cancellationToken = default)
    {
        return AnalyzeCore(objects, rules, parallelOptions, features, cancellationToken, progress, maxTrackedValues);
    }

    /// <summary>
    /// 实际分析主流程：所有并行累计、合并、后置处理均在此实现。
    /// 返回元组 (Result, QuantileSketches?, DistinctSketches?)：草图为 null 表示对应类型无数据。
    /// </summary>
    private static (
        AnalysisResult Result,
        Dictionary<string, MergeableQuantileSketch>? QuantileSketches,
        Dictionary<string, HyperLogLog>? DistinctSketches
    ) AnalyzeCore(
        IEnumerable<Dictionary<string, object?>> objects,
        AnalysisRules? rules,
        ParallelOptions? parallelOptions,
        AdvancedAnalysisOptions? features,
        CancellationToken cancellationToken,
        IProgress<long>? progress = null,
        int? maxTrackedValues = null)
    {
        var state = new AnalysisAccumulator(rules, features, maxTrackedValues ?? MaxTrackedUniqueValues);
        state.ObserveRange(objects, parallelOptions, progress, cancellationToken);
        return state.CompleteDetailed();
    }

    internal static void Accumulate(AnalysisAccumulator state,
        IEnumerable<Dictionary<string, object?>> objects, ParallelOptions? parallelOptions,
        IProgress<long>? progress, CancellationToken cancellationToken)
    {
        int maxTracked = state.MaxTrackedValues;
        var features = state.Features;
        var pathCache = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, parallelOptions?.CancellationToken ?? default);
        cancellationToken = linkedCancellation.Token;
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = parallelOptions?.MaxDegreeOfParallelism ?? -1,
            TaskScheduler = parallelOptions?.TaskScheduler,
            CancellationToken = cancellationToken
        };
        void ProcessChunk(IEnumerable<Dictionary<string, object?>> chunk)
        {
            Parallel.ForEach(
                chunk,
                options,
                () => new AnalyzerThreadState(),
                (obj, loopState, localState) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    localState.TotalObjects++;

                    // Fix #9: Array.Sort avoids LINQ lambda allocations in the per-object hot path
                    var sortedEntries = obj.Select(pair => KeyValuePair.Create(
                        pathCache.GetOrAdd(pair.Key, static key => FieldPath.Parse(key).ToString()), pair.Value)).ToArray();
                    Array.Sort(sortedEntries, static (a, b) => string.Compare(a.Key, b.Key, StringComparison.Ordinal));
                    for (int i = 1; i < sortedEntries.Length; i++)
                    {
                        if (sortedEntries[i - 1].Key == sortedEntries[i].Key)
                            throw new InvalidDataException($"Duplicate structural field path: {sortedEntries[i].Key}");
                    }

                    // 深度分析 相关性收集：每对象一行 (field → double?) 样本；
                    // 仅在 features.EnableCorrelation 为 true 时分配，避免默认路径上增加 GC 压力。
                    Dictionary<string, double?>? correlationRow = features?.EnableCorrelation == true
                        ? new Dictionary<string, double?>(StringComparer.Ordinal)
                        : null;

                    // 将字段写入线程局部可复用字节缓冲区，避免为每个对象分配规范化字符串/byte[]
                    var writer = localState.CanonicalWriter;
                    writer.Clear();
                    foreach (var kv in sortedEntries)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        string fieldName = kv.Key;
                        object? value = kv.Value;
                        var typedValue = RecordValue.FromObject(value);
                        string valStr = typedValue.ToDisplayString();
                        string identity = typedValue.CanonicalKey;

                        WriteAsciiNumber(writer, fieldName.Length);
                        WriteByte(writer, (byte)':');
                        WriteUtf8(writer, fieldName);
                        WriteByte(writer, (byte)'=');
                        WriteAsciiNumber(writer, identity.Length);
                        WriteByte(writer, (byte)':');
                        WriteUtf8(writer, identity);

                        if (!localState.StatsMap.TryGetValue(fieldName, out var stat))
                        {
                            stat = new FieldAccumulator { FieldName = fieldName };
                            stat.Components = state.CreateComponents(fieldName);
                            localState.StatsMap[fieldName] = stat;
                        }

                        stat.Count++;

                        string valType = typedValue.Kind.ToString();
                        stat.TypeCounts[valType] = stat.TypeCounts.GetValueOrDefault(valType) + 1;
                        if (valType == "Null") stat.NullCount++;

                        if (typedValue.Kind == RecordValueKind.Number)
                        {
                            stat.NumbersAreIntegral &= typedValue.IsInteger;
                            stat.NumbersFitInt64 &= typedValue.FitsInt64;
                            stat.NumbersFitDecimal &= typedValue.FitsDecimal;
                            stat.NumbersFitDouble &= typedValue.FitsDouble;
                        }
                        stat.AddValue(typedValue, 1, maxTracked);

                        stat.Components!.Observe(typedValue);
                        RecordFeatureMetrics(localState, value, valStr, fieldName, correlationRow, features);
                    }

                    // 深度分析 相关性：把这一行所有字段的数值样本一次性送给收集器
                    if (correlationRow != null)
                    {
                        (localState.CorrelationCollector ??= new FieldCorrelationCollector(features!.CorrelationReservoir))
                            .RecordObject(correlationRow);
                    }

                    // 128 位非密码学哈希去重（替代慢速 MD5 + 十六进制字符串）
                    var objHash = ComputeHash128(writer.WrittenSpan);

                    state.RecordObject(objHash, () => SerializeDuplicate(sortedEntries));

                    return localState;
                },
                state.MergeThread);
        }

        if (objects is IList<Dictionary<string, object?>> objectList)
        {
            // Fix #3: Use O(1) index-based slice instead of O(n) Skip/Take per chunk
            for (int offset = 0; offset < objectList.Count; offset += ChunkSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = Math.Min(ChunkSize, objectList.Count - offset);
                ProcessChunk(IndexedSlice(objectList, offset, count));
                // P6.1 进度上报：每完成一个分块（10K 对象）触发一次回调，
                // Spectre.Console 的 Progress() 内部已合并高频上报避免闪烁
                progress?.Report(state.TotalObjects);
            }
        }
        else
        {
            foreach (var chunk in EnumerateChunks(objects, ChunkSize))
            {
                cancellationToken.ThrowIfCancellationRequested();
                ProcessChunk(chunk);
                // P6.1 进度上报：未知总量的流式输入同样每分块触发一次
                progress?.Report(state.TotalObjects);
            }
        }

    }

    internal static (AnalysisResult Result, Dictionary<string, MergeableQuantileSketch>? QuantileSketches,
        Dictionary<string, HyperLogLog>? DistinctSketches) Complete(AnalysisAccumulator state)
    {
        var globalStatsMap = new Dictionary<string, FieldAccumulator>(state.Fields, StringComparer.Ordinal);
        var globalMetricsMap = state.Metrics;
        var globalCorrelationCollector = state.CorrelationCollector;
        var globalStringPatterns = state.StringPatterns;
        var globalUnicodeHygiene = state.UnicodeHygiene;
        var features = state.Features;
        var rules = state.Rules;
        var fieldRules = state.FieldRules;
        foreach (string path in state.RulePlan.Paths)
            if (!globalStatsMap.ContainsKey(path))
                globalStatsMap.Add(path, new FieldAccumulator { FieldName = path, Components = state.CreateComponents(path) });
        // 后置处理：合并极值、推断默认值与语义分类
        var results = new List<FieldStatistic>();
        foreach (var field in globalStatsMap.Values)
        {
            var stat = field.ToResult();
            stat.Coverage = state.TotalObjects > 0 ? (double)stat.Count / state.TotalObjects : 0;

            stat.NullAppeared = stat.TypeCounts.ContainsKey("Null");
            stat.PrimaryType = stat.TypeCounts.OrderByDescending(t => t.Value).ThenBy(t => t.Key, StringComparer.Ordinal).FirstOrDefault().Key ?? "String";

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

            field.Components!.Complete(stat);

            // 应用配置规则
            if (fieldRules != null && fieldRules.TryGetValue(stat.FieldName, out var rule))
            {
                stat.AppliedRule = AnalysisAccumulator.Clone(rule);

                if (rule.DefaultValue != null)
                {
                    stat.DefaultValueRepresentation = rule.DefaultValue.ToString();
                }

                if (string.IsNullOrEmpty(rule.PrimaryType) && rule.EnumValues is { Count: > 0 })
                {
                    stat.PrimaryType = InferPrimaryTypeFromEnum(rule.EnumValues);
                }
                else if (!string.IsNullOrEmpty(rule.PrimaryType))
                {
                    stat.PrimaryType = rule.PrimaryType;
                }

            }

            results.Add(stat);
        }

        results.Sort((a, b) =>
        {
            int cmp = b.Count.CompareTo(a.Count);
            if (cmp == 0)
            {
                cmp = string.Compare(a.FieldName, b.FieldName, StringComparison.Ordinal);
            }
            return cmp;
        });

        var duplicates = state.BuildDuplicates();
        long totalUniqueObjects = state.ObjectCounts.Count;
        long totalDuplicateGroups = state.ObjectCounts.Count(kv => kv.Value > 1);

        // 质量检查 v2：枚举/范围/模式/必填/唯一/null 率 + 数据集期望，统一评估
        var qualityViolations = QualityChecks.Evaluate(state.TotalObjects, totalUniqueObjects, results, rules ?? new(), state.RulePlan);

        // 深度分析 后置处理：构建新报告（仅 features 启用时计算，否则全部为 null）
        FieldCorrelationMatrix? correlationMatrix = null;
        Dictionary<string, StringPatternProfile>? stringPatterns = null;
        Dictionary<string, UnicodeHygieneReport>? unicodeHygiene = null;
        Dictionary<string, TimeSeriesAnalysis>? timeSeriesAnalyses = null;

        if (features != null)
        {
            if (features.EnableCorrelation && globalCorrelationCollector != null && globalCorrelationCollector.Seen >= 3)
            {
                // 仅纳入被识别为 Number 的字段，避免对分类变量（如已被语义识别为 DateTime 的字符串）跑相关
                var numericFields = results
                    .Where(s => s.PrimaryType == "Number" && s.Count >= 3)
                    .Select(s => s.FieldName)
                    .ToList();
                if (numericFields.Count >= 2)
                {
                    correlationMatrix = FieldCorrelationCalculator.Compute(globalCorrelationCollector, numericFields);
                }
            }

            if (features.EnableStringPatterns && globalStringPatterns != null)
            {
                stringPatterns = new Dictionary<string, StringPatternProfile>(StringComparer.Ordinal);
                foreach (var (field, profiler) in globalStringPatterns)
                {
                    var profile = profiler.Build();
                    if (profile.TotalSamples > 0)
                    {
                        stringPatterns[field] = profile;
                    }
                }
            }

            if (features.EnableUnicodeHygiene && globalUnicodeHygiene != null)
            {
                unicodeHygiene = new Dictionary<string, UnicodeHygieneReport>(StringComparer.Ordinal);
                foreach (var (field, report) in globalUnicodeHygiene)
                {
                    if (report.TotalSamples > 0)
                    {
                        unicodeHygiene[field] = AnalysisAccumulator.Clone(report);
                    }
                }
            }

            if (features.EnableTimeSeries)
            {
                timeSeriesAnalyses = new Dictionary<string, TimeSeriesAnalysis>(StringComparer.Ordinal);
                foreach (var stat in results)
                {
                    if (stat.DateTimeCount is null || stat.DateHistogram == null) continue;
                    var ts = TimeSeriesAnalyzer.Analyze(stat);
                    if (ts != null && ts.BucketCount >= TimeSeriesAnalyzer.MinBucketsForAnalysis)
                    {
                        timeSeriesAnalyses[stat.FieldName] = ts;
                    }
                }
            }
        }

        // 提取每字段的 t-digest 与 HyperLogLog 草图（仅当确实有数据时），供分布快照 / diff 使用
        Dictionary<string, MergeableQuantileSketch>? quantileSketches = null;
        Dictionary<string, HyperLogLog>? distinctSketches = null;
        foreach (var (field, m) in globalMetricsMap)
        {
            if (m.Quantiles != null && m.Quantiles.TotalWeight > 0)
            {
                (quantileSketches ??= new Dictionary<string, MergeableQuantileSketch>(StringComparer.Ordinal))[field] = MergeableQuantileSketch.Restore(m.Quantiles.Capture());
            }
            if (m.DistinctHll != null && m.DistinctHll.Estimate() > 0)
            {
                (distinctSketches ??= new Dictionary<string, HyperLogLog>(StringComparer.Ordinal))[field] = HyperLogLog.Restore(m.DistinctHll.Capture());
            }
        }

        return (
            new AnalysisResult
            {
                TotalObjects = state.TotalObjects,
                TotalUniqueObjects = totalUniqueObjects,
                TotalDuplicateGroups = totalDuplicateGroups,
                DuplicateObjects = duplicates,
                FieldStatistics = results,
                QualityViolations = qualityViolations,
                CorrelationMatrix = correlationMatrix,
                StringPatterns = stringPatterns,
                UnicodeHygiene = unicodeHygiene,
                TimeSeriesAnalyses = timeSeriesAnalyses
            },
            quantileSketches,
            distinctSketches
        );
    }

    /// <summary>
    /// 深度分析 分析深度增强：把当前字段值登记到相应线程局部收集器中。
    /// 仅在 <paramref name="features"/> 启用了对应特性时执行，否则直接返回。
    /// </summary>
    private static void RecordFeatureMetrics(
        AnalyzerThreadState localState,
        object? value,
        string valStr,
        string fieldName,
        Dictionary<string, double?>? correlationRow,
        AdvancedAnalysisOptions? features)
    {
        if (features == null) return;
        if (!features.EnableCorrelation
            && !features.EnableStringPatterns
            && !features.EnableUnicodeHygiene
            && !features.EnableTimeSeries
            && !features.EnableDistributionSnapshot)
        {
            return;
        }

        // 1) 相关性：收集每字段的数值样本到本对象行（循环结束后再 RecordObject）
        if (correlationRow != null && value != null && TryGetDoubleValue(value, out double dVal))
        {
            correlationRow[fieldName] = dVal;
        }

        // 2) 字符串模式 / Unicode 卫生：仅对字符串值生效
        string? stringValue = value is RecordValue { Kind: RecordValueKind.String } text ? text.Scalar : value as string;
        if (stringValue is { } sVal)
        {
            if (features.EnableStringPatterns)
            {
                var patterns = localState.StringPatterns ??= new Dictionary<string, StringPatternProfiler>(StringComparer.Ordinal);
                if (!patterns.TryGetValue(fieldName, out var profiler))
                {
                    profiler = new StringPatternProfiler();
                    patterns[fieldName] = profiler;
                }
                profiler.Add(sVal);
            }

            if (features.EnableUnicodeHygiene)
            {
                var hygiene = localState.UnicodeHygiene ??= new Dictionary<string, UnicodeHygieneReport>(StringComparer.Ordinal);
                if (!hygiene.TryGetValue(fieldName, out var report))
                {
                    report = new UnicodeHygieneReport();
                    hygiene[fieldName] = report;
                }
                var sample = UnicodeHygieneChecker.Check(sVal);
                UnicodeHygieneChecker.MergeInto(report, sample);
            }
        }
    }

    private static bool TryGetDoubleValue(object val, out double result)
    {
        if (val is RecordValue typed) return typed.TryGetDouble(out result);
        if (val is double d) { result = d; return double.IsFinite(d); }
        if (val is float f) { result = f; return float.IsFinite(f); }
        if (val is int i) { result = i; return true; }
        if (val is long l) { result = l; return true; }
        if (val is decimal dec) { result = (double)dec; return true; }
        if (val is byte b) { result = b; return true; }
        if (val is short s) { result = s; return true; }
        if (val is sbyte sb) { result = sb; return true; }
        if (val is ushort us) { result = us; return true; }
        if (val is uint ui) { result = ui; return true; }
        if (val is ulong ul) { result = ul; return true; }

        result = 0;
        return false;
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
            return RecordValue.FromObject(sortedDict).ToJsonString(indented: true);
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

    private static string SafeToString(object? value)
    {
        if (value == null) return "null";
        if (value is string s) return s;
        try { return value.ToString() ?? ""; }
        catch { return "[unserializable]"; }
    }


    private static string InferPrimaryTypeFromEnum(List<System.Text.Json.JsonElement> enumElements)
    {
        bool any = false, allNumber = true, allBoolean = true;
        foreach (var elem in enumElements)
        {
            any = true;
            if (elem.ValueKind != JsonValueKind.Number) allNumber = false;
            if (elem.ValueKind != JsonValueKind.True && elem.ValueKind != JsonValueKind.False) allBoolean = false;
        }
        if (any && allNumber) return "Number";
        if (any && allBoolean) return "Boolean";
        return "String";
    }

}
