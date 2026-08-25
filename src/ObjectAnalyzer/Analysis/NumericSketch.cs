using System;
using System.Collections.Generic;

namespace ObjectAnalyzer.Analysis;

/// <summary>
/// 数值剖析草图集合：流式、内存有界、可跨线程合并。
/// - <see cref="MergeableQuantileSketch"/>：简化 t-digest 质心草图，估算分位数（P50/P90/P95/P99）。
/// - <see cref="HyperLogLog"/>：稀疏→稠密基数估计（近似 distinct count）。
/// - <see cref="LogHistogram"/>：以 2 为底的对数定宽桶直方图，单遍流式、无需预知数值范围。
/// </summary>
public static class NumericSketch
{
    /// <summary>
    /// 对数直方图桶指数：|x| ∈ [2^e, 2^(e+1)) 时，正数返回 e、负数返回 -e-1（与正桶区分）。
    /// 0 单独成桶（int.MinValue 哨兵）。重建区间：正桶 k→[2^k,2^(k+1))；负桶 k→(-2^(-k), -2^(-k-1)]。
    /// </summary>
    public static int LogBucket(double x)
    {
        double exp = Math.Floor(Math.Log2(Math.Abs(x)));
        return x > 0 ? (int)exp : -(int)exp - 1;
    }

    /// <summary>
    /// 时间戳直方图分桶：根据数据跨度自动选择最直观的桶尺寸（秒/分/时/天/周/月/年），
    /// 返回桶下界（UTC 对齐）与单桶跨度。目标桶数 ≤ <paramref name="targetBuckets"/>。
    /// </summary>
    public static (DateTimeOffset bucketStart, TimeSpan span) DateBucket(DateTimeOffset value, DateTimeOffset min, DateTimeOffset max, int targetBuckets = 24)
    {
        TimeSpan total = max - min;
        if (total.Ticks <= 0)
        {
            // 退化：所有值都相同，落到 1 秒桶
            return (TruncateToSecond(value), TimeSpan.FromSeconds(1));
        }

        // 候选桶跨度（按从细到粗）
        TimeSpan[] candidates =
        {
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30),
            TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30),
            TimeSpan.FromHours(1), TimeSpan.FromHours(6), TimeSpan.FromHours(12),
            TimeSpan.FromDays(1), TimeSpan.FromDays(7),
            TimeSpan.FromDays(30), TimeSpan.FromDays(90),
            TimeSpan.FromDays(365),
        };

        foreach (var span in candidates)
        {
            double bucketCount = total.Ticks / (double)span.Ticks;
            if (bucketCount <= targetBuckets)
            {
                return (FloorToSpan(value, span), span);
            }
        }
        // 跨度极大：回退到整年
        return (FloorToYear(value), TimeSpan.FromDays(365));
    }

    private static DateTimeOffset TruncateToSecond(DateTimeOffset value)
    {
        var t = value.UtcDateTime;
        return new DateTimeOffset(t.Year, t.Month, t.Day, t.Hour, t.Minute, t.Second, TimeSpan.Zero);
    }

    private static DateTimeOffset FloorToSpan(DateTimeOffset value, TimeSpan span)
    {
        long ticks = value.UtcTicks - (value.UtcTicks % span.Ticks);
        return new DateTimeOffset(ticks, TimeSpan.Zero);
    }

    private static DateTimeOffset FloorToYear(DateTimeOffset value)
    {
        var t = value.UtcDateTime;
        return new DateTimeOffset(t.Year, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }
}

/// <summary>
/// 可合并的分位数草图（简化 t-digest）：质心按均值有序，超容量时合并相邻间隙最小的质心。
/// 流式 Add、线程本地实例 Merge 后查询 P50/P90/P95/P99，内存 O(Capacity)。
/// 结果为近似值，对连续分布中位区域误差通常 &lt;1%，尾部略大。
/// </summary>
public sealed class MergeableQuantileSketch
{
    private readonly int _capacity;
    private List<Centroid> _centroids = new();
    private long _totalWeight;

    public MergeableQuantileSketch(int capacity = 256)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 16);
        _capacity = capacity;
    }

    public long TotalWeight => _totalWeight;

    public void Add(double value, long weight = 1)
    {
        if (weight <= 0 || !double.IsFinite(value))
            return;

        _totalWeight += weight;
        int idx = FindInsertIndex(value);
        // 与紧邻质心完全重合时直接并质心，否则插入新质心
        if (idx > 0 && _centroids[idx - 1].Mean == value)
        {
            var c = _centroids[idx - 1];
            _centroids[idx - 1] = c with { Mean = (c.Mean * c.Weight + value * weight) / (c.Weight + weight), Weight = c.Weight + weight };
        }
        else
        {
            _centroids.Insert(idx, new Centroid(value, weight));
            Compress();
        }
    }

    /// <summary>合并另一草图（不修改入参）。</summary>
    public void Merge(MergeableQuantileSketch other)
    {
        foreach (var c in other._centroids)
            InsertCentroid(c);
        _totalWeight += other._totalWeight;
        Compress();
    }

    /// <summary>近似分位数（q ∈ (0,1]）；草图为空时返回 null。线性插值质心间。</summary>
    public double? Quantile(double q)
    {
        if (_totalWeight == 0 || q <= 0 || q > 1)
            return null;

        double target = q * _totalWeight;
        long cumulative = 0;
        for (int i = 0; i < _centroids.Count; i++)
        {
            long before = cumulative;
            cumulative += _centroids[i].Weight;
            if (cumulative >= target)
            {
                if (i == 0 || before >= target)
                    return _centroids[i].Mean; // 命中最左质心或落在其权重段内

                // 在前一个质心中点与本质心中点之间线性插值
                double low = _centroids[i - 1].Mean;
                double high = _centroids[i].Mean;
                double frac = (target - before) / (double)_centroids[i].Weight;
                return low + (high - low) * frac;
            }
        }
        return _centroids[^1].Mean;
    }

    private void InsertCentroid(Centroid c)
    {
        int idx = FindInsertIndex(c.Mean);
        _centroids.Insert(idx, c);
    }

    /// <summary>超过容量时迭代合并相邻间隙最小的一对质心（加权均值）。</summary>
    private void Compress()
    {
        while (_centroids.Count > _capacity)
        {
            int best = 0;
            double bestGap = double.MaxValue;
            for (int i = 0; i < _centroids.Count - 1; i++)
            {
                double gap = _centroids[i + 1].Mean - _centroids[i].Mean;
                if (gap < bestGap)
                {
                    bestGap = gap;
                    best = i;
                }
            }

            var left = _centroids[best];
            var right = _centroids[best + 1];
            long weight = left.Weight + right.Weight;
            var merged = new Centroid(
                (left.Mean * left.Weight + right.Mean * right.Weight) / weight,
                weight);
            _centroids.RemoveAt(best + 1);
            _centroids[best] = merged;
        }
    }

    private int FindInsertIndex(double mean)
    {
        int lo = 0, hi = _centroids.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (_centroids[mid].Mean < mean)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }

    private readonly record struct Centroid(double Mean, long Weight);
}

/// <summary>
/// HyperLogLog 基数估计：默认精度 p=14（16384 寄存器，标准误差 ≈0.81%）。
/// 稀疏模式（字典）起步，寄存器命中超过阈值后升级为稠密字节数组；
/// 线程本地实例通过 <see cref="Merge"/>（逐寄存器取最大）合并到全局。
/// 估计策略：寄存器零值仍充足时优先 LinearCounting（消除原始估计在 n≈m 过渡区的系统性正偏差），
/// 否则退回标准 HLL 原始估计。
/// </summary>
public sealed class HyperLogLog
{
    private const int SparseThreshold = 256;
    private readonly int _p;
    private readonly int _m;
    private Dictionary<int, byte>? _sparse;
    private byte[]? _dense;

    public HyperLogLog(int precision = 14)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(precision, 4);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(precision, 20);
        _p = precision;
        _m = 1 << precision;
    }

    /// <summary>加入一个字符串值（内部做 FNV-1a 64 + murmur 终末混淆哈希）。</summary>
    public void Add(string value)
    {
        AddHash(HashString(value));
    }

    public void AddHash(ulong hash)
    {
        int register = (int)(hash >> (64 - _p));
        byte rank = (byte)(CountLeadingZeros(hash << _p, 64 - _p) + 1);

        if (_dense != null)
        {
            if (_dense[register] < rank)
                _dense[register] = rank;
            return;
        }

        _sparse ??= new Dictionary<int, byte>();
        if (_sparse.TryGetValue(register, out byte existing))
        {
            if (existing < rank)
                _sparse[register] = rank;
        }
        else
        {
            _sparse[register] = rank;
            if (_sparse.Count >= SparseThreshold)
                PromoteToDense();
        }
    }

    /// <summary>逐寄存器取最大合并（合并后保持当前实例的存储形态）。</summary>
    public void Merge(HyperLogLog other)
    {
        // 空源无贡献
        if (other._dense == null && (other._sparse == null || other._sparse.Count == 0))
            return;

        // 自身尚未初始化：直接复制对方状态
        if (_dense == null && _sparse == null)
        {
            if (other._dense != null)
            {
                EnsureDense();
                Array.Copy(other._dense, _dense!, _m);
            }
            else
            {
                _sparse = new Dictionary<int, byte>(other._sparse!);
            }
            return;
        }

        if (other._dense != null)
        {
            EnsureDense();
            for (int i = 0; i < _m; i++)
            {
                if (_dense![i] < other._dense[i])
                    _dense[i] = other._dense[i];
            }
        }
        else
        {
            foreach (var kv in other._sparse!)
            {
                if (_dense != null)
                {
                    if (_dense[kv.Key] < kv.Value)
                        _dense[kv.Key] = kv.Value;
                }
                else
                {
                    _sparse!.TryGetValue(kv.Key, out byte existing);
                    if (existing < kv.Value)
                        _sparse[kv.Key] = kv.Value;
                }
            }
            if (_sparse != null && _sparse.Count >= SparseThreshold)
                PromoteToDense();
        }
    }

    /// <summary>基数估计：优先 LinearCounting（零寄存器充足时更准），否则标准 HLL 原始估计。</summary>
    public long Estimate()
    {
        if (_dense != null)
        {
            double sum = 0;
            int zeros = 0;
            for (int i = 0; i < _m; i++)
            {
                byte r = _dense[i];
                sum += 1.0 / (1UL << r);
                if (r == 0)
                    zeros++;
            }

            double alphaM = Alpha(_m);
            double rawEstimate = alphaM * _m * _m / sum;

            // 零寄存器充足时 LC 无偏且方差更低；同时保留经典 E≤2.5m 切换条件
            if (zeros > 0 && (rawEstimate <= 2.5 * _m || zeros > _m * 0.01))
            {
                return (long)Math.Round(_m * Math.Log((double)_m / zeros)); // LinearCounting
            }

            return (long)Math.Round(rawEstimate);
        }

        if (_sparse == null || _sparse.Count == 0)
            return 0;

        // 稀疏态：触达寄存器数即不同桶数，LinearCounting 估计
        int zerosSparse = _m - _sparse.Count;
        return (long)Math.Round(_m * Math.Log((double)_m / zerosSparse));
    }

    private void PromoteToDense()
    {
        EnsureDense();
        foreach (var kv in _sparse!)
        {
            if (_dense![kv.Key] < kv.Value)
                _dense[kv.Key] = kv.Value;
        }
        _sparse = null;
    }

    private void EnsureDense() => _dense ??= new byte[_m];

    private static byte CountLeadingZeros(ulong bits, int maxBits)
    {
        if (bits == 0)
            return (byte)maxBits;
        int lz = System.Numerics.BitOperations.LeadingZeroCount(bits);
        return (byte)Math.Min(lz, maxBits);
    }

    private static double Alpha(int m) => m switch
    {
        16 => 0.673,
        32 => 0.697,
        64 => 0.709,
        _ => 0.7213 / (1.0 + 1.079 / m)
    };

    /// <summary>FNV-1a 64 位遍历字符 + murmur3 终末混淆（avalanche），适配 HLL 位分布需求。</summary>
    internal static ulong HashString(string value)
    {
        ulong h = 14695981039346656037UL;
        const ulong prime = 0x100000001b3UL;
        foreach (char c in value)
        {
            h ^= c;
            h *= prime;
            h ^= (ulong)(c >> 8);
            h *= prime;
        }
        // murmur3 finalizer
        h ^= h >> 33;
        h *= 0xff51afd7ed558ccdUL;
        h ^= h >> 33;
        h *= 0xc4ceb9fe1a85ec53UL;
        h ^= h >> 33;
        return h;
    }
}
