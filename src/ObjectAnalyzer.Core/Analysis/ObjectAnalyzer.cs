using System.Buffers;
using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ObjectAnalyzer.Analysis;

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
    private static readonly Regex RegexPhone = new(
        @"^1[3-9]\d{9}$", RegexOptions.Compiled); // 中国大陆手机号
    private static readonly Regex RegexIdCard = new(
        @"^\d{15}$|^\d{17}[\dXx]$", RegexOptions.Compiled); // 中国大陆身份证（15/18 位）
    private static readonly Regex RegexBankCard = new(
        @"^\d{16,19}$", RegexOptions.Compiled); // 银行卡号
    private static readonly Regex RegexSsn = new(
        @"^\d{3}-\d{2}-\d{4}$", RegexOptions.Compiled); // 美国社会安全号 SSN（ddd-dd-dddd）
    private static readonly Regex RegexIban = new(
        @"^[A-Za-z]{2}\d{2}[A-Za-z0-9]{10,30}$", RegexOptions.Compiled); // IBAN（紧凑格式，去除空格后匹配）
    private static readonly Regex RegexPassport = new(
        @"^[A-Za-z]{1,2}\d{6,9}$", RegexOptions.Compiled); // 护照号（1-2 位字母 + 6-9 位数字，需字段名提示）

    // 字段名强提示：用于在值形态上辅助判别"姓名"语义（避免仅凭形态误判普通文本）
    private static readonly string[] NameFieldHints =
    {
        "name", "fullname", "realname", "username", "nickname", "displayname",
        "firstname", "lastname", "familyname", "givenname", "surname",
        "姓名", "用户名", "昵称", "联系人", "真实姓名"
    };

    // 护照号字段名提示（护照值形态较宽泛，必须字段名佐证以降低误报）
    private static readonly string[] PassportFieldHints =
    {
        "passport", "passportno", "passportnumber",
        "护照", "护照号"
    };


    /// <summary>
    /// 主流分析入口：返回 <see cref="AnalysisResult"/>，不暴露内部草图。
    /// 需要每字段的 t-digest / HyperLogLog 草图（用于分布快照 / 漂移）时使用 <see cref="AnalyzeDetailed"/>。
    /// </summary>
    public static AnalysisResult Analyze(
        IEnumerable<Dictionary<string, object?>> objects,
        AnalysisRules? rules = null,
        ParallelOptions? parallelOptions = null,
        AnalysisFeatures? features = null,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return AnalyzeCore(objects, rules, parallelOptions, features, cancellationToken, progress).Result;
    }

    /// <summary>
    /// P4 增强入口：在返回 <see cref="AnalysisResult"/> 的同时，暴露每字段的 t-digest 草图与 HyperLogLog 草图，
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
        AnalysisFeatures? features = null,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return AnalyzeCore(objects, rules, parallelOptions, features, cancellationToken, progress);
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
        AnalysisFeatures? features,
        CancellationToken cancellationToken,
        IProgress<long>? progress = null)
    {
        var globalSeenHashes = new ConcurrentDictionary<Hash128, byte>();
        var globalDuplicateJsons = new ConcurrentDictionary<Hash128, string>();

        var globalStatsMap = new Dictionary<string, FieldStatistic>();
        var globalMetricsMap = new Dictionary<string, InternalFieldMetrics>();
        var globalDuplicateCounts = new ConcurrentDictionary<Hash128, long>();
        long globalTotalObjects = 0;
        var mergeLock = new object();

        // P4 全局合并结果（MergeLocalState 持有；Analyze 末尾读取并清空）
        FieldCorrelationCollector? globalCorrelationCollector = null;
        Dictionary<string, StringPatternProfiler>? globalStringPatterns = null;
        Dictionary<string, UnicodeHygieneReport>? globalUnicodeHygiene = null;

        var options = parallelOptions ?? new ParallelOptions();

        void MergeLocalState(AnalyzerThreadState local)
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

                    globalMetrics.MergeFrom(localMetrics);
                }

                // P4 增强：合并线程局部收集器
                // 相关性：每个线程的收集器持有该线程处理的对象的"行样本"切片；
                // 把所有线程的收集器按"对象顺序"追加合并，保证 Pearson/Spearman 计算使用全量数据。
                if (local.CorrelationCollector != null)
                {
                    if (globalCorrelationCollector == null)
                    {
                        globalCorrelationCollector = local.CorrelationCollector;
                    }
                    else if (!ReferenceEquals(local.CorrelationCollector, globalCorrelationCollector))
                    {
                        globalCorrelationCollector.MergeFrom(local.CorrelationCollector);
                    }
                }

                // 字符串模式：合并到 global（按字段求和；不同线程的 patterns 互不重叠，累加即可）
                if (local.StringPatterns != null)
                {
                    if (globalStringPatterns == null)
                    {
                        globalStringPatterns = new Dictionary<string, StringPatternProfiler>(StringComparer.Ordinal);
                    }
                    foreach (var (field, profiler) in local.StringPatterns)
                    {
                        if (!globalStringPatterns.TryGetValue(field, out var dest))
                        {
                            // 复制一份避免线程间共享底层数据；StringPatternProfiler 内部只持有不可变 dict / list
                            dest = new StringPatternProfiler();
                            globalStringPatterns[field] = dest;
                        }
                        dest.MergeFrom(profiler);
                    }
                }

                // Unicode 卫生：每线程的累加直接 MergeInto 到 global
                if (local.UnicodeHygiene != null)
                {
                    if (globalUnicodeHygiene == null)
                    {
                        globalUnicodeHygiene = new Dictionary<string, UnicodeHygieneReport>(StringComparer.Ordinal);
                    }
                    foreach (var (field, report) in local.UnicodeHygiene)
                    {
                        if (!globalUnicodeHygiene.TryGetValue(field, out var dest))
                        {
                            dest = new UnicodeHygieneReport();
                            globalUnicodeHygiene[field] = dest;
                        }
                        UnicodeHygieneChecker.MergeInto(dest, report);
                    }
                }
            }
        }

        void ProcessChunk(IEnumerable<Dictionary<string, object?>> chunk)
        {
            Parallel.ForEach(
                chunk,
                options,
                () => new AnalyzerThreadState(),
                (obj, loopState, localState) =>
                {
                    localState.TotalObjects++;

                    // Fix #9: Array.Sort avoids LINQ lambda allocations in the per-object hot path
                    var sortedEntries = obj.ToArray();
                    Array.Sort(sortedEntries, static (a, b) => string.Compare(a.Key, b.Key, StringComparison.Ordinal));

                    // P4 相关性收集：每对象一行 (field → double?) 样本；
                    // 仅在 features.EnableCorrelation 为 true 时分配，避免默认路径上增加 GC 压力。
                    Dictionary<string, double?>? correlationRow = features?.EnableCorrelation == true
                        ? new Dictionary<string, double?>(StringComparer.Ordinal)
                        : null;

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
                        RecordValueMetrics(value, valStr, fieldName, metrics);

                        // P4 增强：相关性 / 模式 / 卫生（仅在 features 启用时执行）
                        RecordFeatureMetrics(localState, value, valStr, fieldName, correlationRow, features);

                        // 质量检查 v2：minValue/maxValue 越界精确计数（热路径，不受唯一值截断影响）
                        if (rules != null && rules.Fields.TryGetValue(fieldName, out FieldRule? qualityRule))
                        {
                            if (value != null
                                && (qualityRule.MinValue.HasValue || qualityRule.MaxValue.HasValue)
                                && TryGetDoubleValue(value, out double rangeVal)
                                && (rangeVal < qualityRule.MinValue.GetValueOrDefault(double.NegativeInfinity)
                                    || rangeVal > qualityRule.MaxValue.GetValueOrDefault(double.PositiveInfinity)))
                            {
                                metrics.AddRangeViolation();
                            }
                        }
                    }

                    // P4 相关性：把这一行所有字段的数值样本一次性送给收集器
                    if (correlationRow != null)
                    {
                        (localState.CorrelationCollector ??= new FieldCorrelationCollector(features!.CorrelationReservoir))
                            .RecordObject(correlationRow);
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
                // P6.1 进度上报：每完成一个分块（10K 对象）触发一次回调，
                // Spectre.Console 的 Progress() 内部已合并高频上报避免闪烁
                progress?.Report(globalTotalObjects);
            }
        }
        else
        {
            foreach (var chunk in EnumerateChunks(objects, ChunkSize))
            {
                cancellationToken.ThrowIfCancellationRequested();
                ProcessChunk(chunk);
                // P6.1 进度上报：未知总量的流式输入同样每分块触发一次
                progress?.Report(globalTotalObjects);
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

                    // Welford 样本标准差（n-1 分母）
                    if (metrics.NumberCount >= 2)
                    {
                        stat.StdDevNumber = Math.Sqrt(metrics.NumM2 / (metrics.NumberCount - 1));
                    }

                    // 近似分位数（可合并 t-digest 草图）
                    if (metrics.Quantiles != null && metrics.Quantiles.TotalWeight > 0)
                    {
                        stat.MedianNumber = metrics.Quantiles.Quantile(0.50);
                        stat.P90Number = metrics.Quantiles.Quantile(0.90);
                        stat.P95Number = metrics.Quantiles.Quantile(0.95);
                        stat.P99Number = metrics.Quantiles.Quantile(0.99);
                    }

                    // 对数定宽直方图：负数桶升序 → 零桶 → 正数桶升序
                    if (metrics.Histogram != null || metrics.ZeroCount > 0)
                    {
                        stat.Histogram = BuildHistogram(metrics);
                    }

                    // 异常值检测（IQR 1.5×）：复用 P25/P75 + 对数直方图近似归类
                    if (metrics.NumberCount >= 4 && metrics.Quantiles != null)
                    {
                        double? p25 = metrics.Quantiles.Quantile(0.25);
                        double? p75 = metrics.Quantiles.Quantile(0.75);
                        if (p25.HasValue && p75.HasValue)
                        {
                            double iqr = p75.Value - p25.Value;
                            double lower = p25.Value - 1.5 * iqr;
                            double upper = p75.Value + 1.5 * iqr;
                            long outlierCount = CountOutsideHistogram(metrics.Histogram, metrics.ZeroCount, lower, upper);
                            stat.OutlierMethod = "iqr";
                            stat.OutlierBounds = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                                $"[{lower:G4}, {upper:G4}]");
                            stat.OutlierCount = outlierCount;
                            stat.OutlierRatio = metrics.NumberCount > 0 ? (double)outlierCount / metrics.NumberCount : 0;
                        }
                    }
                }

                if (metrics.StringCount > 0)
                {
                    stat.MinLength = metrics.MinLength;
                    stat.MaxLength = metrics.MaxLength;
                    stat.AvgLength = (double)metrics.LengthSum / metrics.StringCount;

                    // 当某语义匹配数量达到字符串总数的 80% 以上时，标注其 SemanticType
                    var topSemantic = metrics.SemanticCounts
                        .OrderByDescending(kv => kv.Value)
                        .FirstOrDefault();

                    if (topSemantic.Value > 0 && (double)topSemantic.Value / metrics.StringCount >= 0.8)
                    {
                        stat.SemanticType = topSemantic.Key;
                    }
                }

                // 日期/时间深度剖析：min/max/count + 按跨度自适应切档直方图
                if (metrics.DateTimeCount > 0 && metrics.MinDateTime is not null && metrics.MaxDateTime is not null)
                {
                    stat.DateTimeCount = metrics.DateTimeCount;
                    stat.MinDateTime = metrics.MinDateTime;
                    stat.MaxDateTime = metrics.MaxDateTime;
                    if (metrics.DateHistogram is { Count: > 0 })
                    {
                        stat.DateHistogram = BuildDateHistogram(metrics.MinDateTime.Value, metrics.MaxDateTime.Value, metrics.DateHistogram);
                    }
                }

                // distinct 值计数：未截断时用精确值；截断时退化为 HyperLogLog 近似估计
                stat.RangeViolationCount = metrics.RangeViolationCount;
                long exactDistinct = stat.ValueCounts.Keys.Count(k => k != "[Other values]");
                if (!stat.ValueCountsTruncated)
                {
                    stat.DistinctCount = exactDistinct;
                }
                else if (metrics.DistinctHll != null && metrics.DistinctHll.Estimate() > 0)
                {
                    stat.DistinctCount = metrics.DistinctHll.Estimate();
                    stat.DistinctApproximate = true;
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

                if (string.IsNullOrEmpty(rule.PrimaryType) && rule.EnumValues is { Count: > 0 })
                {
                    stat.PrimaryType = InferPrimaryTypeFromEnum(rule.EnumValues);
                }
                else if (!string.IsNullOrEmpty(rule.PrimaryType))
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

        // 质量检查 v2：枚举/范围/模式/必填/唯一/null 率 + 数据集期望，统一评估
        var qualityViolations = rules == null
            ? new List<FieldViolation>()
            : QualityChecks.Evaluate(globalTotalObjects, totalUniqueObjects, results, rules);

        // P4 后置处理：构建新报告（仅 features 启用时计算，否则全部为 null）
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
                        unicodeHygiene[field] = report;
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
                (quantileSketches ??= new Dictionary<string, MergeableQuantileSketch>(StringComparer.Ordinal))[field] = m.Quantiles;
            }
            if (m.DistinctHll != null && m.DistinctHll.Estimate() > 0)
            {
                (distinctSketches ??= new Dictionary<string, HyperLogLog>(StringComparer.Ordinal))[field] = m.DistinctHll;
            }
        }

        return (
            new AnalysisResult
            {
                TotalObjects = globalTotalObjects,
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
    /// 构建对数定宽直方图：桶边界为 2 的幂，负数桶升序、零值单独成桶（From=To=0）、正数桶升序。
    /// </summary>
    private static List<NumericHistogramBucket> BuildHistogram(InternalFieldMetrics metrics)
    {
        var buckets = new List<NumericHistogramBucket>();

        if (metrics.Histogram != null)
        {
            foreach (var kv in metrics.Histogram.Where(h => h.Key < 0).OrderBy(h => h.Key))
            {
                // 负数桶 k=-e-1 表示 (-2^(e+1), -2^e]
                double to = -Math.Pow(2, -kv.Key - 1);
                double from = -Math.Pow(2, -kv.Key);
                buckets.Add(new NumericHistogramBucket { From = from, To = to, Count = kv.Value });
            }
        }

        if (metrics.ZeroCount > 0)
        {
            buckets.Add(new NumericHistogramBucket { From = 0, To = 0, Count = metrics.ZeroCount });
        }

        if (metrics.Histogram != null)
        {
            foreach (var kv in metrics.Histogram.Where(h => h.Key >= 0).OrderBy(h => h.Key))
            {
                // 正数桶 k 表示 [2^k, 2^(k+1))
                buckets.Add(new NumericHistogramBucket
                {
                    From = Math.Pow(2, kv.Key),
                    To = Math.Pow(2, kv.Key + 1),
                    Count = kv.Value
                });
            }
        }

        return buckets;
    }

    /// <summary>
    /// 构建按数据跨度自适应切档的时间戳直方图：先按总跨度选定显示桶尺寸，
    /// 再将流式阶段收集的 1 分钟粒度桶向上聚合到目标尺寸。
    /// 目标桶数 ≤ 24。
    /// </summary>
    private static List<DateHistogramBucket> BuildDateHistogram(
        DateTimeOffset min, DateTimeOffset max, Dictionary<long, long> minuteBuckets)
    {
        // 选定显示桶尺寸（任一桶的尺寸都用同一个值，保持等宽）
        var sample = NumericSketch.DateBucket(min, min, max, targetBuckets: 24);
        TimeSpan span = sample.span;
        long spanTicks = span.Ticks;
        long minTicks = min.UtcTicks;

        // 聚合：从 1 分钟桶 → 显示桶（对 minTicks 向下取整到 spanTicks 边界）
        long minBucketStart = (minTicks / spanTicks) * spanTicks;
        var aggregated = new Dictionary<long, long>();
        foreach (var (minuteKey, count) in minuteBuckets)
        {
            long minuteTicks = minuteKey * TimeSpan.TicksPerMinute;
            long displayBucket = (minuteTicks / spanTicks) * spanTicks;
            aggregated[displayBucket] = aggregated.GetValueOrDefault(displayBucket) + count;
        }

        // 补齐 [minBucketStart, max] 范围内所有空桶，便于报告看出"无活动"区间
        long maxTicks = (max.UtcTicks / spanTicks) * spanTicks;
        var result = new List<DateHistogramBucket>();
        for (long t = minBucketStart; t <= maxTicks; t += spanTicks)
        {
            long count = aggregated.GetValueOrDefault(t);
            var from = new DateTimeOffset(t, TimeSpan.Zero);
            var to = new DateTimeOffset(t + spanTicks, TimeSpan.Zero);
            result.Add(new DateHistogramBucket { From = from, To = to, Count = count });
        }
        return result;
    }

    /// <summary>
    /// 基于对数定宽直方图近似归类区间 [lower, upper] 外的样本数。
    /// 桶整体落在区间外时按整桶计入；桶跨越边界时按整桶计入（保守策略）。
    /// </summary>
    private static long CountOutsideHistogram(
        Dictionary<int, long>? histogram, long zeroCount, double lower, double upper)
    {
        long count = 0;
        if (histogram != null)
        {
            foreach (var kv in histogram)
            {
                // 重建桶上下界以判定是否完全在区间外
                double bucketLo, bucketHi;
                if (kv.Key < 0)
                {
                    // 负桶 k=-e-1：(-2^(e+1), -2^e]
                    int exp = -kv.Key - 1;
                    bucketLo = -Math.Pow(2, exp + 1);
                    bucketHi = -Math.Pow(2, exp);
                }
                else
                {
                    // 正桶 k：[2^k, 2^(k+1))
                    bucketLo = Math.Pow(2, kv.Key);
                    bucketHi = Math.Pow(2, kv.Key + 1);
                }

                // 桶完全在区间外：整桶计入；跨越边界：保守起见按整桶计入
                if (bucketHi < lower || bucketLo > upper)
                {
                    count += kv.Value;
                }
            }
        }
        if (zeroCount > 0 && (0 < lower || 0 > upper))
        {
            count += zeroCount;
        }
        return count;
    }

    private static void RecordValueMetrics(object? value, string valStr, string fieldName, InternalFieldMetrics metrics)
    {
        if (value == null) return;

        // 0. distinct 值 HyperLogLog（所有非空标量值，与 ValueCounts 同域）
        metrics.AddDistinct(valStr);

        // 1. 数值统计（Welford 均值/M2 + 分位数草图 + 对数直方图）
        if (TryGetDoubleValue(value, out double dVal))
        {
            metrics.AddNumber(dVal);
        }

        // 2. 字符串度量与语义测试
        if (value is string strVal)
        {
            metrics.MinLength = Math.Min(metrics.MinLength, strVal.Length);
            metrics.MaxLength = Math.Max(metrics.MaxLength, strVal.Length);
            metrics.StringCount++;
            metrics.AddLength(strVal.Length);

            if (RegexUuid.IsMatch(strVal))
                metrics.SemanticCounts["UUID"] = metrics.SemanticCounts.GetValueOrDefault("UUID") + 1;
            else if (RegexEmail.IsMatch(strVal))
                metrics.SemanticCounts["Email"] = metrics.SemanticCounts.GetValueOrDefault("Email") + 1;
            else if (RegexDateTime.IsMatch(strVal) && TryParseDateTimeOffset(strVal, out var dateTimeValue))
            {
                metrics.SemanticCounts["DateTime"] = metrics.SemanticCounts.GetValueOrDefault("DateTime") + 1;
                metrics.AddDateTime(dateTimeValue);
            }
            else if (RegexIdCard.IsMatch(strVal) && PiiValidators.IsValidChineseIdCard(strVal))
                metrics.SemanticCounts["IdCard"] = metrics.SemanticCounts.GetValueOrDefault("IdCard") + 1;
            else if (RegexPhone.IsMatch(strVal))
                metrics.SemanticCounts["Phone"] = metrics.SemanticCounts.GetValueOrDefault("Phone") + 1;
            else if (RegexBankCard.IsMatch(strVal) && PiiValidators.IsValidBankCardLuhn(strVal))
                metrics.SemanticCounts["BankCard"] = metrics.SemanticCounts.GetValueOrDefault("BankCard") + 1;
            else if (RegexSsn.IsMatch(strVal))
                metrics.SemanticCounts["Ssn"] = metrics.SemanticCounts.GetValueOrDefault("Ssn") + 1;
            else if (IsIbanValue(strVal))
                metrics.SemanticCounts["Iban"] = metrics.SemanticCounts.GetValueOrDefault("Iban") + 1;
            else if (RegexPassport.IsMatch(strVal) && ContainsFieldHint(fieldName, PassportFieldHints))
                metrics.SemanticCounts["Passport"] = metrics.SemanticCounts.GetValueOrDefault("Passport") + 1;
            else if (RegexIPv4.IsMatch(strVal))
                metrics.SemanticCounts["IPv4"] = metrics.SemanticCounts.GetValueOrDefault("IPv4") + 1;
            else if (RegexUrl.IsMatch(strVal))
                metrics.SemanticCounts["URL"] = metrics.SemanticCounts.GetValueOrDefault("URL") + 1;
            else if (LooksLikeHumanName(strVal, fieldName))
                metrics.SemanticCounts["Name"] = metrics.SemanticCounts.GetValueOrDefault("Name") + 1;
        }
    }

    /// <summary>
    /// P4 分析深度增强：把当前字段值登记到相应线程局部收集器中。
    /// 仅在 <paramref name="features"/> 启用了对应特性时执行，否则直接返回。
    /// </summary>
    private static void RecordFeatureMetrics(
        AnalyzerThreadState localState,
        object? value,
        string valStr,
        string fieldName,
        Dictionary<string, double?>? correlationRow,
        AnalysisFeatures? features)
    {
        if (features == null || features.IsEmpty) return;

        // 1) 相关性：收集每字段的数值样本到本对象行（循环结束后再 RecordObject）
        if (correlationRow != null && value != null && TryGetDoubleValue(value, out double dVal))
        {
            correlationRow[fieldName] = dVal;
        }

        // 2) 字符串模式 / Unicode 卫生：仅对字符串值生效
        if (value is string sVal)
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

    /// <summary>IBAN 判定：去除空格后匹配紧凑形态，再按 MOD 97-10 验证校验位。</summary>
    private static bool IsIbanValue(string value)
    {
        if (value.Length < 15 || value.Length > 39)
            return false;

        string compact = value.Replace(" ", "");
        return RegexIban.IsMatch(compact) && PiiValidators.IsValidIbanMod97(compact);
    }

    /// <summary>
    /// 字段名包含提示词即命中（大小写不敏感）。用于 passport 等前后缀皆常见的字段
    /// （passport_no / myPassport / 护照号），提示词本身语义足够明确，无误报风险。
    /// </summary>
    private static bool ContainsFieldHint(string fieldName, string[] hints)
    {
        foreach (string hint in hints)
        {
            if (fieldName.Contains(hint, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool LooksLikeHumanName(string value, string fieldName)
    {
        if (!IsNameFieldName(fieldName))
            return false;

        if (value.Length < 2 || value.Length > 40)
            return false;

        int letterCount = 0;
        foreach (char c in value)
        {
            if (char.IsLetter(c))
            {
                letterCount++;
                continue;
            }
            if (c is ' ' or '-' or '.')
                continue;
            return false;
        }
        return letterCount > 0;
    }

    private static bool IsNameFieldName(string fieldName)
    {
        foreach (string hint in NameFieldHints)
        {
            int hintLength = hint.Length;
            if (fieldName.Length < hintLength)
                continue;
            if (!fieldName.EndsWith(hint, StringComparison.OrdinalIgnoreCase))
                continue;
            if (fieldName.Length == hintLength)
                return true;
            char before = fieldName[fieldName.Length - hintLength - 1];
            if (before is '.' or '_' or '-')
                return true;
        }
        return false;
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
    /// 解析日期时间字符串为 DateTimeOffset。支持 ISO 8601 三种形态：
    /// 带时区（"Z" / "+08:00"）、无时区（按 UTC 解释）、纯日期（按 UTC 00:00:00）。
    /// 解析失败返回 false。
    /// </summary>
    private static bool TryParseDateTimeOffset(string value, out DateTimeOffset result)
    {
        if (DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            result = parsed;
            return true;
        }
        if (DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var dateOnly))
        {
            result = new DateTimeOffset(dateOnly, TimeSpan.Zero);
            return true;
        }
        result = default;
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
