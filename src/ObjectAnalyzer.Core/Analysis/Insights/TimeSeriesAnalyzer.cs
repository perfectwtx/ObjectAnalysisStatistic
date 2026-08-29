namespace ObjectAnalyzer.Analysis;

/// <summary>
/// 时间序列分析器：复用 <see cref="FieldStatistic.DateHistogram"/> 输出，
/// 在分析后置阶段计算趋势、周期、间隙、脉冲性。
/// </summary>
public static class TimeSeriesAnalyzer
{
    /// <summary>序列点过少时直接返回 "insufficient" 而不分析。</summary>
    public const int MinBucketsForAnalysis = 6;

    /// <summary>周期自相关最大滞后（桶数）。</summary>
    public const int MaxPeriodLag = 60;

    /// <summary>周期强度阈值（自相关峰值 ≥ 此值视为有效周期）。</summary>
    public const double PeriodStrengthThreshold = 0.4;

    /// <summary>趋势 R² 阈值（R² ≥ 此值且斜率显著才视为 "rising/falling"，否则 "stable"）。</summary>
    public const double TrendSignificanceThreshold = 0.4;

    /// <summary>从 <see cref="FieldStatistic.DateHistogram"/> 构建 <see cref="TimeSeriesAnalysis"/>。</summary>
    public static TimeSeriesAnalysis? Analyze(FieldStatistic stat)
    {
        if (stat.DateHistogram == null || stat.DateHistogram.Count < MinBucketsForAnalysis)
            return null;
        if (stat.MinDateTime is null || stat.MaxDateTime is null) return null;

        var series = stat.DateHistogram
            .OrderBy(b => b.From)
            .Select(b => new TimeSeriesBucket { From = b.From, To = b.To, Count = b.Count })
            .ToList();

        var first = series.First();
        var last = series.Last();
        TimeSpan bucketSpan = first.To - first.From;

        var ts = new TimeSeriesAnalysis
        {
            Start = first.From,
            End = last.To,
            BucketSpan = bucketSpan,
            BucketCount = series.Count,
            TotalSamples = stat.DateTimeCount ?? series.Sum(s => s.Count),
            Series = series
        };

        int nonEmpty = series.Count(s => s.Count > 0);
        ts.EmptyRatio = (double)(series.Count - nonEmpty) / series.Count;

        // 1) 趋势（普通最小二乘 + Theil-Sen）
        var counts = series.Select(s => (double)s.Count).ToArray();
        var (slope, r2) = LinearRegression(counts);
        ts.TrendSlope = slope;
        ts.TrendRSquared = r2;

        if (counts.Length >= 10)
        {
            ts.TheilSenSlope = TheilSenSlope(counts);
        }

        // 判定趋势方向（用斜率符号 + R²；slope 接近 0 视为平稳）
        if (counts.Length < MinBucketsForAnalysis)
        {
            ts.Trend = "insufficient";
        }
        else if (r2 >= TrendSignificanceThreshold && Math.Abs(slope) > counts.Average() * 0.02)
        {
            ts.Trend = slope > 0 ? "rising" : "falling";
        }
        else
        {
            ts.Trend = "stable";
        }

        // 2) 周期检测（自相关 + 局部最大）
        if (counts.Length >= MaxPeriodLag)
        {
            var (period, strength) = DetectPeriod(counts);
            if (period.HasValue && strength >= PeriodStrengthThreshold)
            {
                ts.DetectedPeriod = period;
                ts.PeriodicityStrength = strength;
            }
        }

        // 3) 间隙检测（连续空桶作为一个 gap）
        int longestRun = 0, currentRun = 0;
        foreach (var s in series)
        {
            if (s.Count == 0)
            {
                currentRun++;
                if (currentRun > longestRun) longestRun = currentRun;
            }
            else
            {
                currentRun = 0;
            }
        }
        ts.LongestGapBuckets = longestRun;

        // 4) 脉冲性 = 峰值 / 中位数（无样本序列返回 1.0）
        long max = (long)counts.Max();
        long med = Median(counts);
        ts.Burstiness = med > 0 ? (double)max / med : 1.0;

        return ts;
    }

    /// <summary>普通最小二乘：返回斜率与 R²。空数据时返回 (0, 0)。</summary>
    public static (double slope, double r2) LinearRegression(double[] y)
    {
        int n = y.Length;
        if (n < 2) return (0, 0);
        double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0;
        for (int i = 0; i < n; i++)
        {
            sumX += i;
            sumY += y[i];
            sumXY += i * y[i];
            sumX2 += (double)i * i;
        }
        double meanX = sumX / n;
        double meanY = sumY / n;
        double cov = sumXY - n * meanX * meanY;
        double varX = sumX2 - n * meanX * meanX;
        double varY = 0;
        for (int i = 0; i < n; i++) varY += (y[i] - meanY) * (y[i] - meanY);
        if (varX <= 0) return (0, 0);

        double slope = cov / varX;
        // R² = 1 - SSres/SStot
        double ssRes = 0;
        for (int i = 0; i < n; i++)
        {
            double yhat = meanY + slope * (i - meanX);
            ssRes += (y[i] - yhat) * (y[i] - yhat);
        }
        double r2 = varY > 0 ? 1.0 - ssRes / varY : 0;
        if (r2 < 0) r2 = 0;
        if (r2 > 1) r2 = 1;
        return (slope, r2);
    }

    /// <summary>Theil-Sen 斜率：所有 i&lt;j 配对的中位数斜率，对异常值稳健。O(n²)。</summary>
    public static double? TheilSenSlope(double[] y)
    {
        int n = y.Length;
        if (n < 2) return null;
        var slopes = new List<double>(n * (n - 1) / 2);
        for (int i = 0; i < n; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                slopes.Add((y[j] - y[i]) / (j - i));
            }
        }
        slopes.Sort();
        return slopes[slopes.Count / 2];
    }

    /// <summary>周期检测：自相关函数 + 在 [2, MaxPeriodLag] 找首个强峰。</summary>
    public static (int? period, double strength) DetectPeriod(double[] y)
    {
        int n = y.Length;
        if (n < MaxPeriodLag) return (null, 0);

        double mean = y.Average();
        double var = 0;
        for (int i = 0; i < n; i++) var += (y[i] - mean) * (y[i] - mean);
        if (var <= 0) return (null, 0);

        double bestVal = 0;
        int bestLag = 0;
        for (int lag = 2; lag <= MaxPeriodLag; lag++)
        {
            double cov = 0;
            for (int i = 0; i + lag < n; i++) cov += (y[i] - mean) * (y[i + lag] - mean);
            double r = cov / var;
            if (r > bestVal)
            {
                bestVal = r;
                bestLag = lag;
            }
        }
        if (bestVal >= PeriodStrengthThreshold)
        {
            return (bestLag, bestVal);
        }
        return (null, bestVal);
    }

    private static long Median(double[] values)
    {
        if (values.Length == 0) return 0;
        var sorted = values.OrderBy(v => v).ToArray();
        int mid = sorted.Length / 2;
        return (long)Math.Round(sorted.Length % 2 == 0
            ? (sorted[mid - 1] + sorted[mid]) / 2.0
            : sorted[mid]);
    }
}
