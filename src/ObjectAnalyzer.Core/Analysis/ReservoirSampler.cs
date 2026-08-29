namespace ObjectAnalyzer.Analysis;

/// <summary>
/// 蓄水池抽样（Algorithm R）：对未知长度的流式序列在线维护容量为 capacity 的均匀随机样本。
/// 仅需 O(capacity) 内存；调用方可在 NextSlot 返回 -1 时跳过昂贵的解析/转换，
/// 从而对大流量数据（如逐行 JSONL）显著降低耗时。
/// </summary>
internal sealed class ReservoirSampler<T>(int capacity, Random? random = null)
{
    private readonly int _capacity = capacity;
    private readonly List<T> _items = new();
    private readonly Random _random = random ?? Random.Shared;
    private long _seen;

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
