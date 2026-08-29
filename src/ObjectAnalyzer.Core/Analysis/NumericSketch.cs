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
