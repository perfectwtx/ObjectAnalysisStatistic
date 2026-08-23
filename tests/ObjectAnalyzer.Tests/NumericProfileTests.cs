namespace ObjectAnalyzer.Tests;

using System.Globalization;
using System.Text.Json;

/// <summary>
/// 数值/语义统计深度测试：Welford 标准差、t-digest 分位数、对数直方图、
/// 字符串平均长度与 HyperLogLog distinct 估计。
/// </summary>
public class NumericProfileTests
{
    // ──────────────────────────────────────────────
    // Welford 均值 / 样本标准差
    // ──────────────────────────────────────────────

    [Fact]
    public void Welford_MeanAndStdDev_MatchClosedForm()
    {
        var objects = new List<Dictionary<string, object?>>();
        for (int i = 1; i <= 100; i++)
        {
            objects.Add(new Dictionary<string, object?> { ["v"] = (double)i });
        }

        var stat = Stat(Analyze(objects), "v");

        Assert.Equal(50.5, stat.AvgNumber!.Value, 9);
        // 样本方差 = Σ(x-μ)²/(n-1) = 83325/99
        double expectedStdDev = Math.Sqrt(83325.0 / 99);
        Assert.Equal(expectedStdDev, stat.StdDevNumber!.Value, 6);
    }

    [Fact]
    public void StdDev_Null_WhenFewerThanTwoSamples()
    {
        var objects = new List<Dictionary<string, object?>>
        {
            new() { ["v"] = 42 }
        };

        var stat = Stat(Analyze(objects), "v");

        Assert.NotNull(stat.AvgNumber);
        Assert.Null(stat.StdDevNumber);
    }

    // ──────────────────────────────────────────────
    // 分位数（可合并 t-digest 草图）
    // ──────────────────────────────────────────────

    [Fact]
    public void Quantiles_UniformDistribution_WithinTolerance()
    {
        const int n = 10000;
        var objects = new List<Dictionary<string, object?>>(n);
        for (int i = 0; i < n; i++)
        {
            objects.Add(new Dictionary<string, object?> { ["x"] = (double)i });
        }

        var stat = Stat(Analyze(objects), "x");

        AssertQuantile(stat.MedianNumber, expected: (n - 1) * 0.50, tolerancePct: 0.02);
        AssertQuantile(stat.P90Number!, expected: (n - 1) * 0.90, tolerancePct: 0.02);
        AssertQuantile(stat.P95Number!, expected: (n - 1) * 0.95, tolerancePct: 0.02);
        AssertQuantile(stat.P99Number!, expected: (n - 1) * 0.99, tolerancePct: 0.02);

        Assert.True(stat.MedianNumber! <= stat.P90Number!);
        Assert.True(stat.P90Number! <= stat.P95Number!);
        Assert.True(stat.P95Number! <= stat.P99Number!);
    }

    [Fact]
    public void Quantiles_SkewedDistribution_MonotonicAndTailAware()
    {
        var objects = new List<Dictionary<string, object?>>();
        for (int i = 1; i <= 2000; i++)
        {
            objects.Add(new Dictionary<string, object?> { ["y"] = (double)(i * i) });
        }

        var stat = Stat(Analyze(objects), "y");

        Assert.True(stat.MedianNumber! < stat.P90Number!);
        Assert.True(stat.P90Number! < stat.P95Number!);
        Assert.True(stat.P95Number! < stat.P99Number!);
        // 重尾分布下 P99 应显著大于 P50
        Assert.True(stat.P99Number! > stat.MedianNumber! * 2);
        // P99 应接近最大值区域（max = 4,000,000）
        Assert.True(stat.P99Number! > stat.MaxNumber!.Value * 0.5);
    }

    [Fact]
    public void QuantileSketch_Merge_EqualsSinglePass()
    {
        var whole = new MergeableQuantileSketch();
        var first = new MergeableQuantileSketch();
        var second = new MergeableQuantileSketch();

        for (int i = 0; i < 1000; i++)
        {
            double v = i * 3.7;
            whole.Add(v);
            if (i % 2 == 0)
                first.Add(v);
            else
                second.Add(v);
        }

        first.Merge(second);

        foreach (double q in new[] { 0.25, 0.50, 0.90, 0.99 })
        {
            double a = whole.Quantile(q)!.Value;
            double b = first.Quantile(q)!.Value;
            Assert.True(Math.Abs(a - b) < Math.Max(1.0, a * 0.01),
                $"q={q}: single-pass={a}, merged={b}");
        }
    }

    // ──────────────────────────────────────────────
    // 对数直方图
    // ──────────────────────────────────────────────

    [Fact]
    public void Histogram_BucketCountsSum_ToNumberCount_AndOrdered()
    {
        var objects = new List<Dictionary<string, object?>>
        {
            new() { ["v"] = -10.0 },
            new() { ["v"] = -3.0 },
            new() { ["v"] = 0 },
            new() { ["v"] = 0 },
            new() { ["v"] = 1 },
            new() { ["v"] = 5 },
            new() { ["v"] = 7 },
            new() { ["v"] = 300 }
        };

        var stat = Stat(Analyze(objects), "v");
        var histogram = stat.Histogram!;

        long total = histogram.Sum(b => b.Count);
        Assert.Equal(8, total); // 桶计数总和 == 数值出现次数

        for (int i = 1; i < histogram.Count; i++)
        {
            Assert.True(histogram[i].From >= histogram[i - 1].To || histogram[i].From > histogram[i - 1].From,
                $"桶未按数值升序排列: index {i}");
        }

        var zeroBucket = histogram.Single(b => b.From == 0 && b.To == 0);
        Assert.Equal(2, zeroBucket.Count);

        // -10 ∈ (-16,-4]，-3 ∈ (-4,-2]：负数桶共 2 个值
        Assert.Equal(2, histogram.Where(b => b.From < 0).Sum(b => b.Count));
    }

    [Fact]
    public void Histogram_Null_ForNonNumericField()
    {
        var objects = new List<Dictionary<string, object?>>
        {
            new() { ["s"] = "hello" }
        };

        var stat = Stat(Analyze(objects), "s");

        Assert.Null(stat.Histogram);
        Assert.Null(stat.StdDevNumber);
        Assert.Null(stat.MedianNumber);
    }

    // ──────────────────────────────────────────────
    // 字符串平均长度
    // ──────────────────────────────────────────────

    [Fact]
    public void AvgLength_ComputedCorrectly()
    {
        var objects = new List<Dictionary<string, object?>>
        {
            new() { ["s"] = "a" },
            new() { ["s"] = "abc" },
            new() { ["s"] = "abcde" },
            new() { ["s"] = null } // null 不参与长度统计
        };

        var stat = Stat(Analyze(objects), "s");

        Assert.Equal(3.0, stat.AvgLength!.Value, 9);
        Assert.Equal(1, stat.MinLength);
        Assert.Equal(5, stat.MaxLength);
    }

    // ──────────────────────────────────────────────
    // HyperLogLog distinct 计数
    // ──────────────────────────────────────────────

    [Fact]
    public void DistinctCount_Exact_WhenBelowTrackingThreshold()
    {
        var objects = new List<Dictionary<string, object?>>();
        for (int i = 0; i < 200; i++)
        {
            objects.Add(new Dictionary<string, object?> { ["id"] = $"u{i % 50}" }); // 50 个唯一值，重复 4 次
        }

        var stat = Stat(Analyze(objects), "id");

        Assert.Equal(50, stat.DistinctCount);
        Assert.False(stat.DistinctApproximate);
        Assert.False(stat.ValueCountsTruncated);
    }

    [Fact]
    public void DistinctCount_Approximate_WhenTruncated()
    {
        const int uniqueValues = 2500;
        var objects = new List<Dictionary<string, object?>>(uniqueValues * 2);
        for (int i = 0; i < uniqueValues * 2; i++)
        {
            objects.Add(new Dictionary<string, object?> { ["id"] = $"user-{i % uniqueValues}" });
        }

        var stat = Stat(Analyze(objects), "id");

        Assert.True(stat.ValueCountsTruncated);
        Assert.True(stat.DistinctApproximate);
        Assert.NotNull(stat.DistinctCount);

        // HLL(p=12) 标准误差 ≈1.6%，放宽到 ±10%
        double estimate = stat.DistinctCount!.Value;
        Assert.True(Math.Abs(estimate - uniqueValues) < uniqueValues * 0.10,
            $"HLL estimate {estimate} vs truth {uniqueValues}");
    }

    [Fact]
    public void HyperLogLog_UnitAccuracy_AndDuplicateStability()
    {
        // 多组同规模样本取平均误差，避免单组输入落入估计器过渡区的固有抖动
        const int n = 10000;
        double totalError = 0;
        const int groups = 5;
        for (int g = 0; g < groups; g++)
        {
            var hll = new HyperLogLog();
            for (int i = 0; i < n; i++)
            {
                hll.Add($"g{g}-val-{i}");
            }

            totalError += Math.Abs(hll.Estimate() - n) / (double)n;
        }

        double meanError = totalError / groups;
        Assert.True(meanError < 0.02, $"HLL mean error {meanError:P2} exceeds 2%");

        // 重复值不应推高基数
        var singleton = new HyperLogLog();
        for (int i = 0; i < 100; i++)
        {
            singleton.Add("same-value");
        }
        Assert.True(singleton.Estimate() <= 2);
    }

    [Fact]
    public void HyperLogLog_SparseMerge_MatchesDenseResult()
    {
        var left = new HyperLogLog();
        var right = new HyperLogLog();
        var combined = new HyperLogLog();

        for (int i = 0; i < 100; i++)
        {
            string v = $"item-{i}";
            combined.Add(v);
            if (i % 2 == 0)
                left.Add(v);
            else
                right.Add(v);
        }

        left.Merge(right);

        Assert.Equal(combined.Estimate(), left.Estimate());
    }

    // ──────────────────────────────────────────────
    // 并行合并一致性
    // ──────────────────────────────────────────────

    [Fact]
    public void ParallelAnalysis_ProducesConsistentDeepStats()
    {
        const int n = 20000;
        var objects = new List<Dictionary<string, object?>>(n);
        // 确定性乱序：跨 chunk 打散数值，强制多线程合并路径
        for (int i = 0; i < n; i++)
        {
            int v = (i * 48271) % n;
            objects.Add(new Dictionary<string, object?> { ["v"] = (double)v, ["tag"] = $"t{v % 97}" });
        }

        var runA = Analyze(objects);
        var runB = Analyze(objects);
        var statA = Stat(runA, "v");
        var statB = Stat(runB, "v");

        // 数值精确指标两次运行一致（浮点合并顺序可能引入微小舍入差，用相对容差）
        double avgA = statA.AvgNumber!.Value;
        Assert.True(Math.Abs(avgA - statB.AvgNumber!.Value) <= 1e-9 * Math.Max(1, Math.Abs(avgA)));
        double stdDevA = statA.StdDevNumber!.Value;
        Assert.True(Math.Abs(stdDevA - statB.StdDevNumber!.Value) <= 1e-6 * stdDevA);
        double sumA = statA.SumNumber!.Value;
        Assert.True(Math.Abs(sumA - statB.SumNumber!.Value) <= 1e-9 * Math.Abs(sumA));

        // 近似分位数两次运行一致且贴近真值（中位数 ≈ 10000）
        AssertQuantile(statA.MedianNumber, expected: n / 2.0, tolerancePct: 0.05);
        double medianA = statA.MedianNumber!.Value;
        double medianB = statB.MedianNumber!.Value;
        Assert.True(Math.Abs(medianA - medianB) <= n * 0.01,
            $"两次运行分位数不稳定: {medianA} vs {medianB}");

        // tag 列 97 个唯一值 → 精确 distinct
        var tagStat = Stat(runA, "tag");
        Assert.Equal(97, tagStat.DistinctCount);
        Assert.False(tagStat.DistinctApproximate);
    }

    // ──────────────────────────────────────────────
    // 序列化 & 脱敏克隆保留新字段
    // ──────────────────────────────────────────────

    [Fact]
    public void Serialization_RoundTrips_NewFields()
    {
        var objects = new List<Dictionary<string, object?>>
        {
            new() { ["v"] = 1, ["s"] = "abc" },
            new() { ["v"] = 2, ["s"] = "abcdef" },
            new() { ["v"] = 3, ["s"] = "ab" },
            new() { ["v"] = 10, ["s"] = "abcd" }
        };

        var result = Analyze(objects);
        string json = JsonSerializer.Serialize(result, JsonSharedOptionsWrite);
        var restored = JsonSerializer.Deserialize<AnalysisResult>(json, JsonSharedOptionsRead);

        var stat = Stat(restored!, "v");
        Assert.Equal(Stat(result, "v").StdDevNumber, stat.StdDevNumber);
        Assert.Equal(Stat(result, "v").MedianNumber, stat.MedianNumber);
        Assert.NotNull(stat.Histogram);
        Assert.Equal(4, stat.Histogram!.Sum(b => b.Count));

        var sStat = Stat(restored!, "s");
        Assert.Equal(Stat(result, "s").AvgLength, sStat.AvgLength);
    }

    [Fact]
    public void PiiRedactor_Clone_PreservesDeepStats()
    {
        var result = Analyze(new List<Dictionary<string, object?>>
        {
            new() { ["amount"] = 100.0, ["note"] = "hello" },
            new() { ["amount"] = 300.0, ["note"] = "hi" }
        });

        var redacted = PiiRedactor.Redact(result);

        var originalAmount = Stat(result, "amount");
        var redactedAmount = Stat(redacted, "amount");
        Assert.Equal(originalAmount.StdDevNumber, redactedAmount.StdDevNumber);
        Assert.Equal(originalAmount.MedianNumber, redactedAmount.MedianNumber);
        Assert.Equal(originalAmount.P99Number, redactedAmount.P99Number);
        Assert.Equal(originalAmount.Histogram!.Count, redactedAmount.Histogram!.Count);

        var redactedNote = Stat(redacted, "note");
        Assert.Equal(Stat(result, "note").AvgLength, redactedNote.AvgLength);
        Assert.Equal(Stat(result, "note").DistinctCount, redactedNote.DistinctCount);
    }

    [Fact]
    public void CsvExporter_IncludesNewColumns()
    {
        var result = Analyze(new List<Dictionary<string, object?>>
        {
            new() { ["v"] = 5, ["s"] = "xyz" },
            new() { ["v"] = 15, ["s"] = "xy" },
            new() { ["v"] = 25, ["s"] = "xy" }
        });

        string path = Path.Combine(Path.GetTempPath(), $"oa_csv_{Guid.NewGuid():N}.csv");
        try
        {
            CsvReportExporter.Save(result, path);
            string[] lines = File.ReadAllLines(path);
            string header = lines.First(l => l.StartsWith("FieldName,", StringComparison.Ordinal));

            Assert.Contains("StdDevNumber", header);
            Assert.Contains("MedianNumber", header);
            Assert.Contains("Histogram", header);
            Assert.Contains("AvgLength", header);
            Assert.Contains("DistinctApproximate", header);

            string dataRow = lines.First(l => l.StartsWith("v,", StringComparison.Ordinal));
            string[] cells = ParseCsvRow(dataRow);
            int stdDevIdx = Array.IndexOf(ParseCsvRow(header), "StdDevNumber");
            Assert.False(string.IsNullOrEmpty(cells[stdDevIdx]));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void FormatQuantiles_And_BucketLabels_AreWellFormed()
    {
        var objects = new List<Dictionary<string, object?>>
        {
            new() { ["v"] = 5 }, new() { ["v"] = 500 }, new() { ["v"] = -6 }
        };

        var stat = Stat(Analyze(objects), "v");

        string quantiles = stat.FormatQuantiles()!;
        Assert.Contains("P50", quantiles);
        Assert.Contains("P99", quantiles);

        var histogram = stat.Histogram!;
        // 数据 {5, 500, -6} → 桶 (-8,-4]、[4,8)、[256,512)，无零桶
        Assert.Equal(3, histogram.Count);
        var positiveBucket = histogram.Single(b => b.From == 4); // [4,8)
        Assert.Equal("[4,8)", positiveBucket.ToString());
        var negativeBucket = histogram.Single(b => b.From < 0); // (-8,-4]
        Assert.Equal("(-8,-4]", negativeBucket.ToString());
    }

    // ──────────────────────────────────────────────
    // 工具方法
    // ──────────────────────────────────────────────

    private static readonly JsonSerializerOptions JsonSharedOptionsRead = new()
    {
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonSerializerOptions JsonSharedOptionsWrite = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static FieldStatistic Stat(AnalysisResult result, string fieldName)
        => result.FieldStatistics.First(f => f.FieldName == fieldName);

    private static void AssertQuantile(double? actual, double expected, double tolerancePct)
    {
        Assert.NotNull(actual);
        double tolerance = Math.Abs(expected) * tolerancePct + 0.5;
        Assert.True(Math.Abs(actual.Value - expected) <= tolerance,
            string.Create(CultureInfo.InvariantCulture, $"quantile {actual} vs expected {expected}"));
    }

    /// <summary>极简 CSV 行解析：仅处理本导出器产生的双引号包裹字段。</summary>
    private static string[] ParseCsvRow(string line)
    {
        var cells = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '"')
            {
                inQuotes = true;
            }
            else if (c == ',')
            {
                cells.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }
        cells.Add(current.ToString());
        return cells.ToArray();
    }
}
