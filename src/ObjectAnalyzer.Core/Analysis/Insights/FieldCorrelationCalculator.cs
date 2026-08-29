namespace ObjectAnalyzer.Analysis;

/// <summary>
/// 字段相关性矩阵计算器。基于 <see cref="FieldCorrelationCollector"/> 收集的样本，
/// 两两计算 Pearson / Spearman 系数。
/// </summary>
public static class FieldCorrelationCalculator
{
    /// <summary>显著相关阈值（|r| ≥ this 视为强相关）。</summary>
    public const double StrongThreshold = 0.7;

    /// <summary>最小样本数：低于此值的相关对在 strongPairs 中不展示（统计不可靠）。</summary>
    public const long MinSamplesForStrong = 30;

    /// <summary>从 <see cref="FieldCorrelationCollector"/> 构建相关性矩阵。样本不足时返回的矩阵元素为 null。</summary>
    public static FieldCorrelationMatrix Compute(FieldCorrelationCollector collector, IEnumerable<string>? numericFields = null)
    {
        var matrix = new FieldCorrelationMatrix();
        if (collector == null || collector.Seen == 0) return matrix;

        // 仅保留至少有 3 个非空样本的字段
        var fields = (numericFields ?? collector.Samples.Keys)
            .Where(f => collector.Samples.TryGetValue(f, out var list) && list.Count(v => v.HasValue) >= 3)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        matrix.Fields = fields;
        matrix.Samples = collector.Seen;

        int n = fields.Count;
        for (int i = 0; i < n; i++)
        {
            var rowP = new List<double?>(n);
            var rowS = new List<double?>(n);
            var rowN = new List<long>(n);
            for (int j = 0; j < n; j++) { rowP.Add(null); rowS.Add(null); rowN.Add(0); }
            matrix.Pearson.Add(rowP);
            matrix.Spearman.Add(rowS);
            matrix.PairwiseSampleCounts.Add(rowN);
        }

        // 把每字段样本数组预提取为本地
        var data = fields.Select(f => collector.Samples[f].ToArray()).ToArray();

        for (int i = 0; i < n; i++)
        {
            matrix.Pearson[i][i] = 1.0;
            matrix.Spearman[i][i] = 1.0;
            matrix.PairwiseSampleCounts[i][i] = data[i].Count(v => v.HasValue);

            for (int j = i + 1; j < n; j++)
            {
                var (pearson, nPair) = Pearson(data[i], data[j]);
                var (spearman, _) = Spearman(data[i], data[j]);

                matrix.Pearson[i][j] = pearson;
                matrix.Pearson[j][i] = pearson;
                matrix.Spearman[i][j] = spearman;
                matrix.Spearman[j][i] = spearman;
                matrix.PairwiseSampleCounts[i][j] = nPair;
                matrix.PairwiseSampleCounts[j][i] = nPair;

                if (pearson is not null && Math.Abs(pearson.Value) >= StrongThreshold && nPair >= MinSamplesForStrong)
                {
                    matrix.StrongPairs.Add(new StrongCorrelationPair
                    {
                        FieldA = fields[i],
                        FieldB = fields[j],
                        Pearson = pearson,
                        Spearman = spearman,
                        Samples = nPair
                    });
                }
            }
        }

        matrix.StrongPairs = matrix.StrongPairs
            .OrderByDescending(p => Math.Abs(p.Pearson ?? 0))
            .ToList();
        return matrix;
    }

    /// <summary>Pearson 线性相关系数。两数组按相同索引配对；任一为 null 的样本被排除。</summary>
    public static (double? r, long n) Pearson(double?[] a, double?[] b)
    {
        int len = Math.Min(a.Length, b.Length);
        double sumX = 0, sumY = 0, sumX2 = 0, sumY2 = 0, sumXY = 0;
        long n = 0;
        for (int i = 0; i < len; i++)
        {
            if (!a[i].HasValue || !b[i].HasValue) continue;
            double x = a[i]!.Value, y = b[i]!.Value;
            sumX += x; sumY += y;
            sumX2 += x * x; sumY2 += y * y;
            sumXY += x * y;
            n++;
        }
        if (n < 3) return (null, n);

        double meanX = sumX / n;
        double meanY = sumY / n;
        double cov = sumXY - n * meanX * meanY;
        double varX = sumX2 - n * meanX * meanX;
        double varY = sumY2 - n * meanY * meanY;
        if (varX <= 0 || varY <= 0) return (null, n);
        double r = cov / Math.Sqrt(varX * varY);
        // 钳制到 [-1, 1]，避免浮点误差
        if (r > 1) r = 1;
        if (r < -1) r = -1;
        return (r, n);
    }

    /// <summary>
    /// Spearman 秩相关系数。算法：先对每列做"中位秩"排名（相同值用平均秩），再 Pearson。
    /// </summary>
    public static (double? r, long n) Spearman(double?[] a, double?[] b)
    {
        int len = Math.Min(a.Length, b.Length);
        // 抽取有效对
        var pairs = new List<(double x, double y)>(len);
        for (int i = 0; i < len; i++)
        {
            if (a[i].HasValue && b[i].HasValue)
                pairs.Add((a[i]!.Value, b[i]!.Value));
        }
        if (pairs.Count < 3) return (null, pairs.Count);

        var xs = pairs.Select(p => p.x).ToArray();
        var ys = pairs.Select(p => p.y).ToArray();
        var ranksX = Rank(xs);
        var ranksY = Rank(ys);

        double sumX = 0, sumY = 0, sumX2 = 0, sumY2 = 0, sumXY = 0;
        for (int i = 0; i < pairs.Count; i++)
        {
            double x = ranksX[i], y = ranksY[i];
            sumX += x; sumY += y;
            sumX2 += x * x; sumY2 += y * y;
            sumXY += x * y;
        }
        long n = pairs.Count;
        double meanX = sumX / n;
        double meanY = sumY / n;
        double cov = sumXY - n * meanX * meanY;
        double varX = sumX2 - n * meanX * meanX;
        double varY = sumY2 - n * meanY * meanY;
        if (varX <= 0 || varY <= 0) return (null, n);
        double r = cov / Math.Sqrt(varX * varY);
        if (r > 1) r = 1;
        if (r < -1) r = -1;
        return (r, n);
    }

    /// <summary>中位秩：相同值取平均秩。返回长度与输入相同的秩数组。</summary>
    private static double[] Rank(double[] values)
    {
        int n = values.Length;
        var idx = Enumerable.Range(0, n).ToArray();
        Array.Sort(idx, (a, b) => values[a].CompareTo(values[b]));
        var ranks = new double[n];
        int i = 0;
        while (i < n)
        {
            int j = i;
            while (j + 1 < n && values[idx[j + 1]] == values[idx[i]]) j++;
            // 排名 (i+1 .. j+1) 的平均
            double avg = (i + 1.0 + j + 1.0) / 2.0;
            for (int k = i; k <= j; k++) ranks[idx[k]] = avg;
            i = j + 1;
        }
        return ranks;
    }
}
