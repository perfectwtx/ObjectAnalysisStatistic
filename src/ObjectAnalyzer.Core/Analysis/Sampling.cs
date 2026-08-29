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
