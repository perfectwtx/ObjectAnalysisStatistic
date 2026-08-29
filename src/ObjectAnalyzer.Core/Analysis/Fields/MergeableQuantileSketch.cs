using System;
using System.Collections.Generic;

namespace ObjectAnalyzer.Analysis;

/// <summary>
/// 可合并的分位数草图（简化 t-digest）：质心按均值有序，超容量时合并相邻间隙最小的质心。
/// 流式 Add、线程本地实例 Merge 后查询 P50/P90/P95/P99，内存 O(Capacity)。
/// 结果为近似值，对连续分布中位区域误差通常 &lt;1%，尾部略大。
/// </summary>
public sealed class MergeableQuantileSketch(int capacity = 256)
{
    private readonly int _capacity = ValidateCapacity(capacity);
    private List<Centroid> _centroids = new();
    private long _totalWeight;

    private static int ValidateCapacity(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 16);
        return capacity;
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
