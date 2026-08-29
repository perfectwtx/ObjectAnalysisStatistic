using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ObjectAnalyzer.Analysis;

/// <summary>
/// 超大文件采样模式。用于在 50GB+ 级别数据导出上快速获得高准确度的 Schema 预测概览：
/// 仅分析样本子集，内存占用有界（≤ 蓄水池容量 / 等距步长间隔），避免全量读取与统计。
/// </summary>
public static class Sampling
{
    /// <summary>是否启用了采样（蓄水池或等距任一）。</summary>
    public static bool IsEnabled(int reservoir, int step) => reservoir > 0 || step > 0;

    /// <summary>校验采样参数：负数、同时启用、以及无效的步长/容量均报错。</summary>
    public static void Validate(int reservoir, int step)
    {
        if (reservoir < 0 || step < 0)
            throw new ArgumentOutOfRangeException(nameof(reservoir), "采样参数不能为负数。");
        if (reservoir > 0 && step > 0)
            throw new InvalidOperationException("--sample-reservoir 与 --sample-step 不能同时使用，请二选一。");
        if (reservoir == 1)
            throw new ArgumentOutOfRangeException(nameof(reservoir), "蓄水池容量至少为 2，否则单个样本无法可靠预估 Schema。");
        if (step == 1)
            throw new ArgumentOutOfRangeException(nameof(step), "--sample-step 必须大于 1（1 表示全量，无需采样）。");
    }

    /// <summary>
    /// 对已解析的对象流做采样包装（适用于整份已物化或流式产出对象的场景）。
    /// 蓄水池：均匀随机取 reservoir 个；等距：每隔 step 条取 1 条。两者互斥。
    /// </summary>
    public static IEnumerable<Dictionary<string, object?>> Apply(
        IEnumerable<Dictionary<string, object?>> source,
        int reservoir,
        int step)
    {
        Validate(reservoir, step);
        if (reservoir > 0)
            return ReservoirSample(source, reservoir);
        if (step > 0)
            return SystematicSample(source, step);
        return source;
    }

    private static IEnumerable<Dictionary<string, object?>> ReservoirSample(
        IEnumerable<Dictionary<string, object?>> source,
        int reservoir)
    {
        var sampler = new ReservoirSampler<Dictionary<string, object?>>(reservoir);
        foreach (var obj in source)
        {
            int slot = sampler.NextSlot();
            if (slot >= 0)
                sampler.SetSlot(slot, obj);
        }
        return sampler.Items;
    }

    private static IEnumerable<Dictionary<string, object?>> SystematicSample(
        IEnumerable<Dictionary<string, object?>> source,
        int step)
    {
        long index = 0;
        foreach (var obj in source)
        {
            if (index % step == 0)
                yield return obj;
            index++;
        }
    }
}

/// <summary>
/// 蓄水池抽样（Algorithm R）：对未知长度的流式序列在线维护容量为 capacity 的均匀随机样本。
/// 仅需 O(capacity) 内存；调用方可在 NextSlot 返回 -1 时跳过昂贵的解析/转换，
/// 从而对大流量数据（如逐行 JSONL）显著降低耗时。
/// </summary>
internal sealed class ReservoirSampler<T>
{
    private readonly int _capacity;
    private readonly List<T> _items = new();
    private readonly Random _random;
    private long _seen;

    public ReservoirSampler(int capacity, Random? random = null)
    {
        _capacity = capacity;
        _random = random ?? Random.Shared;
    }

    public int Count => _items.Count;

    public IReadOnlyList<T> Items => _items;

    /// <summary>
    /// 对当前待判定输入（第 _seen 个元素）作准入/替换决策并推进计数。
    /// 返回值：应存放该元素的槽位（0..capacity-1），或 -1 表示丢弃跳过。
    /// </summary>
    public int NextSlot()
    {
        long n = _seen++;
        if (n < _capacity)
            return _items.Count;
        long r = _random.NextInt64(n + 1);
        return r < _capacity ? (int)r : -1;
    }

    /// <summary>把元素存入 NextSlot 返回的槽位（追加或替换）。</summary>
    public void SetSlot(int slot, T item)
    {
        if (_items.Count <= slot)
            _items.Add(item);
        else
            _items[slot] = item;
    }
}

/// <summary>
/// 采样不确定性 / 置信区间结果：对启用 sampling 时的统计可信度做量化。
/// 经典 Wilsonscore 95% 置信区间：低覆盖字段的可信度可据此判定（覆盖率 0% 同样有意义 —— 区间上界可能仅 5%）。
/// </summary>
public sealed class SamplingConfidence
{
    /// <summary>采样方式："reservoir" / "systematic" / null（全量分析时不填充）。</summary>
    [JsonPropertyName("samplingMethod")]
    public string? SamplingMethod { get; set; }

    /// <summary>有效样本数（reservoir=容量，systematic=总行/step）。</summary>
    [JsonPropertyName("effectiveSampleSize")]
    public long EffectiveSampleSize { get; set; }

    /// <summary>原始记录数（sampling 之前估算的输入总行数）。</summary>
    [JsonPropertyName("estimatedTotal")]
    public long EstimatedTotal { get; set; }

    /// <summary>采样比例（0~1，reservoir=sample/total，systematic=1/step）。</summary>
    [JsonPropertyName("samplingFraction")]
    public double SamplingFraction { get; set; }

    /// <summary>95% 置信水平（用于 Wilson 区间 / 比例误差）。</summary>
    [JsonPropertyName("confidenceLevel")]
    public double ConfidenceLevel { get; set; } = 0.95;

    /// <summary>各字段的覆盖率 95% 置信区间。key = 字段名，value = (lo, hi)。</summary>
    [JsonPropertyName("coverageIntervals")]
    public Dictionary<string, CoverageInterval> CoverageIntervals { get; set; } = new();

    /// <summary>覆盖率低可信的字段（区间宽度 ≥ 0.2 或样本数为 0）。</summary>
    [JsonPropertyName("lowConfidenceFields")]
    public List<string> LowConfidenceFields { get; set; } = new();

    /// <summary>采样方法的整体描述（供人类阅读）。</summary>
    [JsonPropertyName("summary")]
    public string Summary { get; set; } = "";
}

/// <summary>单字段覆盖率 Wilson 95% 置信区间。</summary>
public sealed class CoverageInterval
{
    /// <summary>采样观察到的覆盖率（点估计）。</summary>
    [JsonPropertyName("observed")]
    public double Observed { get; set; }

    /// <summary>区间下界。</summary>
    [JsonPropertyName("lower")]
    public double Lower { get; set; }

    /// <summary>区间上界。</summary>
    [JsonPropertyName("upper")]
    public double Upper { get; set; }

    /// <summary>区间宽度（upper − lower）。</summary>
    [JsonPropertyName("width")]
    public double Width { get; set; }

    /// <summary>区间下界为 0 时为 true（"完全没观察到" 含义模糊：可能是真没出现，也可能是采样运气差）。</summary>
    [JsonPropertyName("ambiguous")]
    public bool Ambiguous { get; set; }
}

/// <summary>
/// 采样置信区间计算器：基于 Wilson score interval（Wald 的小样本稳健替代）。
/// </summary>
public static class SamplingConfidenceCalculator
{
    /// <summary>区间宽度 ≥ 此值视为"低可信"。</summary>
    public const double LowConfidenceWidth = 0.2;

    /// <summary>95% 置信水平对应的 z 值（双侧）。</summary>
    private const double Z95 = 1.96;

    /// <summary>为单字段的"出现 / 样本"对计算 Wilson 95% 置信区间。</summary>
    public static CoverageInterval Compute(double observed, long sampleSize, double confidenceLevel = 0.95)
    {
        var ci = new CoverageInterval { Observed = observed };
        if (sampleSize <= 0)
        {
            ci.Ambiguous = true;
            return ci;
        }
        double z = confidenceLevel >= 0.99 ? 2.576 : Z95;
        double p = Math.Clamp(observed, 0.0, 1.0);
        double n = sampleSize;
        double z2 = z * z;
        double denom = 1.0 + z2 / n;
        double center = (p + z2 / (2 * n)) / denom;
        double margin = z * Math.Sqrt((p * (1 - p) + z2 / (4 * n)) / n) / denom;
        ci.Lower = Math.Max(0.0, center - margin);
        ci.Upper = Math.Min(1.0, center + margin);
        ci.Width = ci.Upper - ci.Lower;
        ci.Ambiguous = ci.Upper < 0.05;
        return ci;
    }

    /// <summary>从 (FieldName, Count, Total) 元组列表构建 SamplingConfidence。</summary>
    public static SamplingConfidence Build(
        string method,
        long sampleSize,
        long estimatedTotal,
        IEnumerable<(string Field, long Count)> fieldCounts,
        long total)
    {
        var sc = new SamplingConfidence
        {
            SamplingMethod = method,
            EffectiveSampleSize = sampleSize,
            EstimatedTotal = estimatedTotal,
            SamplingFraction = estimatedTotal > 0 ? (double)sampleSize / estimatedTotal : 1.0
        };
        if (total > 0)
        {
            foreach (var (field, count) in fieldCounts)
            {
                double obs = (double)count / total;
                var ci = Compute(obs, total);
                sc.CoverageIntervals[field] = ci;
                if (ci.Width >= LowConfidenceWidth || ci.Ambiguous)
                {
                    sc.LowConfidenceFields.Add(field);
                }
            }
        }
        sc.Summary = $"采样方式: {method}; 有效样本: {sampleSize}; 原始规模(估): {estimatedTotal}; 比例: {sc.SamplingFraction:P2}。" +
            (sc.LowConfidenceFields.Count > 0
                ? $" 提示: {sc.LowConfidenceFields.Count} 个字段覆盖率置信区间过宽，统计可信度低。"
                : " 所有字段覆盖率置信区间均较窄（宽度 < 0.2），统计可信。");
        return sc;
    }
}