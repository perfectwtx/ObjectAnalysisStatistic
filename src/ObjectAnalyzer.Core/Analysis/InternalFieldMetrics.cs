namespace ObjectAnalyzer.Analysis;

// 内部使用的线程局部扩展指标集
internal sealed class InternalFieldMetrics
{
    public double MinNumber { get; set; } = double.MaxValue;
    public double MaxNumber { get; set; } = double.MinValue;
    public double SumNumber { get; set; }
    public int NumberCount { get; set; }

    /// <summary>Welford 在线均值（流式，数值稳定）。</summary>
    public double NumMean { get; private set; }

    /// <summary>Welford 累积平方偏差 Σ(x-mean)²，用于样本标准差。</summary>
    public double NumM2 { get; private set; }

    public int MinLength { get; set; } = int.MaxValue;
    public int MaxLength { get; set; } = int.MinValue;

    /// <summary>字符串长度总和（配合 StringCount 得平均长度）。</summary>
    public long LengthSum { get; private set; }

    public int StringCount { get; set; }

    /// <summary>零值出现次数（对数直方图中零单独成桶）。</summary>
    public long ZeroCount { get; private set; }

    public Dictionary<string, int> SemanticCounts { get; } = new();

    // 深度剖析草图（懒创建：仅该字段真的出现对应类型值时分配）
    public MergeableQuantileSketch? Quantiles { get; private set; }
    public Dictionary<int, long>? Histogram { get; private set; } // 非零数值的 log2 桶 → 计数
    public HyperLogLog? DistinctHll { get; private set; }

    // ── 日期/时间剖析草图（懒创建）──
    /// <summary>DateTimeOffset 出现次数。</summary>
    public long DateTimeCount { get; private set; }

    /// <summary>最小时间戳（UTC，无时区输入按 UTC 解释）。</summary>
    public DateTimeOffset? MinDateTime { get; private set; }

    /// <summary>最大时间戳（UTC）。</summary>
    public DateTimeOffset? MaxDateTime { get; private set; }

    /// <summary>
    /// 时间戳直方图（1 分钟固定粒度收集，post-processing 时按跨度自适应切档）。
    /// key = 自 epoch 起的整分钟数 (long)，value = 落入该分钟桶的次数。
    /// </summary>
    public Dictionary<long, long>? DateHistogram { get; private set; }

    public void AddDateTime(DateTimeOffset value)
    {
        DateTimeOffset utc = value.ToUniversalTime();
        if (MinDateTime is null || utc < MinDateTime.Value) MinDateTime = utc;
        if (MaxDateTime is null || utc > MaxDateTime.Value) MaxDateTime = utc;
        DateTimeCount++;

        var hist = DateHistogram ??= new Dictionary<long, long>();
        long bucket = utc.UtcTicks / TimeSpan.TicksPerMinute;
        hist[bucket] = hist.GetValueOrDefault(bucket) + 1;
    }

    public void AddNumber(double value)
    {
        if (!double.IsFinite(value))
            return;

        if (value < MinNumber) MinNumber = value;
        if (value > MaxNumber) MaxNumber = value;
        SumNumber += value;
        NumberCount++;

        // Welford-Knuth 在线更新
        double delta = value - NumMean;
        NumMean += delta / NumberCount;
        NumM2 += delta * (value - NumMean);

        // 分位数草图与直方图
        (Quantiles ??= new MergeableQuantileSketch()).Add(value);
        if (value == 0)
        {
            ZeroCount++;
        }
        else
        {
            int bucket = NumericSketch.LogBucket(value);
            var hist = Histogram ??= new Dictionary<int, long>();
            hist[bucket] = hist.GetValueOrDefault(bucket) + 1;
        }
    }

    public void AddLength(int length)
    {
        LengthSum += length;
    }

    /// <summary>规则 minValue/maxValue 越界计数（热路径精确统计，不受唯一值截断影响）。</summary>
    public void AddRangeViolation() => RangeViolationCount++;

    public long RangeViolationCount { get; private set; }

    public void AddDistinct(string formattedValue)
    {
        (DistinctHll ??= new HyperLogLog()).Add(formattedValue);
    }

    /// <summary>线程局部指标合并（Welford 并行合并公式 + 草图逐项合并）。</summary>
    public void MergeFrom(InternalFieldMetrics other)
    {
        if (other.MinNumber < MinNumber) MinNumber = other.MinNumber;
        if (other.MaxNumber > MaxNumber) MaxNumber = other.MaxNumber;

        if (other.NumberCount > 0)
        {
            int newCount = NumberCount + other.NumberCount;
            double deltaMean = other.NumMean - NumMean;
            if (NumberCount > 0)
            {
                NumM2 += other.NumM2 + deltaMean * deltaMean * NumberCount * other.NumberCount / newCount;
                NumMean += deltaMean * other.NumberCount / newCount;
            }
            else
            {
                NumM2 += other.NumM2;
                NumMean = other.NumMean;
            }
            SumNumber += other.SumNumber;
            NumberCount = newCount;
        }

        if (other.MinLength < MinLength) MinLength = other.MinLength;
        if (other.MaxLength > MaxLength) MaxLength = other.MaxLength;
        LengthSum += other.LengthSum;
        StringCount += other.StringCount;
        ZeroCount += other.ZeroCount;
        RangeViolationCount += other.RangeViolationCount;

        foreach (var sc in other.SemanticCounts)
        {
            SemanticCounts[sc.Key] = SemanticCounts.GetValueOrDefault(sc.Key) + sc.Value;
        }

        if (other.Quantiles != null)
        {
            (Quantiles ??= new MergeableQuantileSketch()).Merge(other.Quantiles);
        }

        if (other.Histogram != null)
        {
            var hist = Histogram ??= new Dictionary<int, long>();
            foreach (var kv in other.Histogram)
            {
                hist[kv.Key] = hist.GetValueOrDefault(kv.Key) + kv.Value;
            }
        }

        if (other.DistinctHll != null)
        {
            (DistinctHll ??= new HyperLogLog()).Merge(other.DistinctHll);
        }

        // 日期/时间统计合并
        if (other.MinDateTime is not null)
        {
            if (MinDateTime is null || other.MinDateTime.Value < MinDateTime.Value)
                MinDateTime = other.MinDateTime;
        }
        if (other.MaxDateTime is not null)
        {
            if (MaxDateTime is null || other.MaxDateTime.Value > MaxDateTime.Value)
                MaxDateTime = other.MaxDateTime;
        }
        DateTimeCount += other.DateTimeCount;
        if (other.DateHistogram != null)
        {
            var dateHist = DateHistogram ??= new Dictionary<long, long>();
            foreach (var kv in other.DateHistogram)
            {
                dateHist[kv.Key] = dateHist.GetValueOrDefault(kv.Key) + kv.Value;
            }
        }
    }
}
