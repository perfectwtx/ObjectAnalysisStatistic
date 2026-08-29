using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace ObjectAnalyzer.Analysis;

/// <summary>
/// 字段间相关性矩阵：两两计算 Pearson 线性相关系数与 Spearman 秩相关系数。
/// 设计取舍：相关性需要"同一对象的多个字段在同一时刻的取值"——无法仅凭字段级预聚合统计反推，
/// 因此分析阶段需为每个对象按字段抽取数值形成"行样本"（最多保留 N 行以控内存）。
/// </summary>
public sealed class FieldCorrelationMatrix
{
    /// <summary>参与相关性计算的字段名（顺序与矩阵行列一致）。</summary>
    [JsonPropertyName("fields")]
    public List<string> Fields { get; set; } = new();

    /// <summary>行样本数（用于相关性估算的样本数）。</summary>
    [JsonPropertyName("samples")]
    public long Samples { get; set; }

    /// <summary>每对字段实际参与计算的样本数（行内两个字段均非空的行数）。</summary>
    [JsonPropertyName("pairwiseSampleCounts")]
    public List<List<long>> PairwiseSampleCounts { get; set; } = new();

    /// <summary>字段对的 Pearson 系数矩阵（行 = i，列 = j，与 fields 同序）；null 表示样本不足。</summary>
    [JsonPropertyName("pearson")]
    public List<List<double?>> Pearson { get; set; } = new();

    /// <summary>字段对的 Spearman 秩系数矩阵。</summary>
    [JsonPropertyName("spearman")]
    public List<List<double?>> Spearman { get; set; } = new();

    /// <summary>
    /// 显著相关性对：|Pearson| ≥ 阈值（默认 0.7）且样本数 ≥ 30 时的强相关字段对，
    /// 便于快速浏览（无需查看完整矩阵）。
    /// </summary>
    [JsonPropertyName("strongPairs")]
    public List<StrongCorrelationPair> StrongPairs { get; set; } = new();
}

/// <summary>一对强相关字段。</summary>
public sealed class StrongCorrelationPair
{
    [JsonPropertyName("fieldA")] public string FieldA { get; set; } = "";
    [JsonPropertyName("fieldB")] public string FieldB { get; set; } = "";
    [JsonPropertyName("pearson")] public double? Pearson { get; set; }
    [JsonPropertyName("spearman")] public double? Spearman { get; set; }
    [JsonPropertyName("samples")] public long Samples { get; set; }
}

/// <summary>
/// 字段相关性收集器：分析阶段在 ObjectAnalyzer 中按需实例化，
/// 为每个对象保留一行（字段 → 数值）样本，超出上限时 Reservoir Sampling 随机采样。
/// </summary>
public sealed class FieldCorrelationCollector
{
    private readonly int _maxRows;
    private readonly Random _rng;
    private long _seen;

    /// <summary>按字段名索引的样本（list[i] 与 list[j] 对应同一对象的两个字段值）。</summary>
    private readonly Dictionary<string, List<double?>> _samples = new(StringComparer.Ordinal);

    public FieldCorrelationCollector(int maxRows = 20_000, int? seed = null)
    {
        _maxRows = maxRows;
        _rng = seed.HasValue ? new Random(seed.Value) : new Random();
    }

    public int MaxRows => _maxRows;
    public long Seen => _seen;
    public IReadOnlyDictionary<string, List<double?>> Samples => _samples;

    /// <summary>在流式阶段调用：注册本次对象中某字段的数值（null 表示缺失或非数值）。</summary>
    public void RecordObject(IReadOnlyDictionary<string, double?> row)
    {
        _seen++;
        if (_samples.Count == 0)
        {
            // 第一次：直接建表
            foreach (var kv in row)
            {
                var list = new List<double?>(_maxRows) { kv.Value };
                _samples[kv.Key] = list;
            }
            return;
        }

        // 已有表：仅追加已知字段（未知字段首次出现则创建）
        foreach (var (field, value) in row)
        {
            if (!_samples.TryGetValue(field, out var list))
            {
                list = new List<double?>(_maxRows);
                for (int k = 0; k < _seen - 1; k++) list.Add(null);
                list.Add(value);
                _samples[field] = list;
            }
            else if (list.Count < _maxRows)
            {
                list.Add(value);
            }
            else
            {
                // Reservoir sampling
                int idx = _rng.Next(0, (int)Math.Min(int.MaxValue, _seen));
                if (idx < _maxRows) list[idx] = value;
            }
        }
        // 缺失字段补 null
        foreach (var field in _samples.Keys.ToList())
        {
            if (!row.ContainsKey(field))
            {
                var list = _samples[field];
                if (list.Count < _maxRows) list.Add(null);
            }
        }
    }

    public void Clear()
    {
        _samples.Clear();
        _seen = 0;
    }

    /// <summary>
    /// 把另一个线程的收集器的样本追加到当前实例。每个字段的样本列表按时间顺序拼接——
    /// 假设两个收集器对应的对象集合不重叠（并行分区处理同一数据集的不同切片）。
    /// 注意：本方法**不安全**用于两个独立数据集的合并（会破坏"对象内同序号字段对齐"的语义）。
    /// </summary>
    public void MergeFrom(FieldCorrelationCollector other)
    {
        if (other == null || other._seen == 0) return;
        long otherSeen = other._seen;
        long baseSeen = _seen;

        // 新字段（首次在 other 中出现）需要先在 self 中按 baseSeen 个 null 占位，再追加 other 的样本
        foreach (var (field, otherList) in other._samples)
        {
            if (!_samples.TryGetValue(field, out var selfList))
            {
                selfList = new List<double?>(_maxRows);
                for (int i = 0; i < baseSeen; i++) selfList.Add(null);
                selfList.AddRange(otherList.Take(_maxRows));
                _samples[field] = selfList;
            }
            else
            {
                // 已有字段：把 other 的样本追加到 self 末尾
                int remaining = _maxRows - selfList.Count;
                if (remaining <= 0) break;
                if (otherList.Count <= remaining)
                {
                    selfList.AddRange(otherList);
                }
                else
                {
                    // Reservoir-style 截断：保留 self 已有样本 + 等概率抽样 other 的前 remaining 个
                    selfList.AddRange(otherList.Take(remaining));
                }
            }
        }

        // 旧字段（在 self 中已有但 other 中没有出现）：补 otherSeen 个 null
        foreach (var field in _samples.Keys.ToList())
        {
            if (other._samples.ContainsKey(field)) continue;
            var selfList = _samples[field];
            int remaining = _maxRows - selfList.Count;
            if (remaining <= 0) continue;
            int pad = (int)Math.Min(otherSeen, remaining);
            for (int i = 0; i < pad; i++) selfList.Add(null);
        }

        _seen += otherSeen;
    }
}

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
