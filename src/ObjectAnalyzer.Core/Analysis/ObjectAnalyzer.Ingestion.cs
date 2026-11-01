using ObjectAnalyzer.Analysis.Options;

namespace ObjectAnalyzer.Analysis;

public static partial class ObjectAnalyzer
{
    internal static void Accumulate(AnalysisAccumulator state,
        IEnumerable<Dictionary<string, object?>> objects, ParallelOptions? parallelOptions,
        IProgress<long>? progress, CancellationToken cancellationToken)
    {
        int maxTracked = state.MaxTrackedValues;
        var features = state.Features;
        var limits = state.Limits;
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, parallelOptions?.CancellationToken ?? default);
        cancellationToken = linkedCancellation.Token;
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Min(parallelOptions?.MaxDegreeOfParallelism is > 0 ? parallelOptions.MaxDegreeOfParallelism : limits.MaxWorkers, limits.MaxWorkers),
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
                        FieldPath.Parse(pair.Key).ToString(), pair.Value)).ToArray();
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
                            state.RegisterField(fieldName);
                            stat = new FieldAccumulator(limits) { FieldName = fieldName };
                            stat.Components = state.CreateComponents(fieldName, global: false);
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
                        RecordFeatureMetrics(localState, value, valStr, fieldName, correlationRow, features, limits);
                    }

                    // 深度分析 相关性：把这一行所有字段的数值样本一次性送给收集器
                    if (correlationRow != null)
                    {
                        (localState.CorrelationCollector ??= new FieldCorrelationCollector(Math.Min(features!.CorrelationReservoir, limits.MaxCorrelationRows), maxFields: limits.MaxCorrelationFields))
                            .RecordObject(correlationRow);
                    }

                    // 128 位非密码学哈希去重（替代慢速 MD5 + 十六进制字符串）
                    var objHash = ComputeHash128(writer.WrittenSpan);

                    state.RecordObject(objHash, () => SerializeDuplicate(sortedEntries));

                    return localState;
                },
                state.MergeThread);
        }

        foreach (var chunk in EnumerateBoundedChunks(objects, limits, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProcessChunk(chunk);
            progress?.Report(state.TotalObjects);
        }
    }

    internal static long EstimateRecordBytes(IReadOnlyDictionary<string, object?> record, AnalysisResourceLimits limits)
    {
        if (record.Count > limits.MaxFields) throw new AnalysisResourceLimitException("record-fields", limits.MaxFields);
        long bytes = 64;
        foreach (var pair in record)
        {
            if (pair.Key.Length > limits.MaxPathCharacters) throw new AnalysisResourceLimitException("path-characters", limits.MaxPathCharacters);
            bytes = checked(bytes + pair.Key.Length * 2L + RetainedPayloadBudget.Size(RecordValue.FromObject(pair.Value)));
            if (bytes > limits.MaxRecordCharacters * 2L) throw new AnalysisResourceLimitException("record-characters", limits.MaxRecordCharacters);
        }
        return bytes;
    }

    private static IEnumerable<List<Dictionary<string, object?>>> EnumerateBoundedChunks(
        IEnumerable<Dictionary<string, object?>> source, AnalysisResourceLimits limits, CancellationToken cancellationToken)
    {
        var chunk = new List<Dictionary<string, object?>>(); long bytes = 0;
        foreach (var record in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long size = EstimateRecordBytes(record, limits);
            if (size > limits.MaxBatchBytes) throw new AnalysisResourceLimitException("batch-bytes", limits.MaxBatchBytes);
            if (chunk.Count > 0 && (chunk.Count >= limits.BatchSize || bytes + size > limits.MaxBatchBytes))
            { yield return chunk; chunk = new(); bytes = 0; }
            chunk.Add(record); bytes += size;
        }
        if (chunk.Count > 0) yield return chunk;
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
        AdvancedAnalysisOptions? features, AnalysisResourceLimits limits)
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
                    profiler = new StringPatternProfiler(limits);
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
}
