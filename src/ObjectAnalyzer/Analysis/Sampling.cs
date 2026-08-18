using System;
using System.Collections.Generic;

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