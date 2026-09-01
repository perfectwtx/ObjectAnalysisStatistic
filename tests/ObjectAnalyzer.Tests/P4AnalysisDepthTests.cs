namespace ObjectAnalyzer.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ObjectAnalyzer.Analysis;
using ObjectAnalyzer.Analysis.Options;
using Xunit;

/// <summary>
/// P4 分析深度增强测试：字段相关性、模糊去重、字符串模式、Unicode 卫生、时间序列、
/// 分布快照 / diff、采样置信区间、AnalysisRunner 集成（CLI 端到端）。
/// </summary>
[Collection(AnalysisRunnerSequentialGroup.Name)]
public class P4AnalysisDepthTests
{
    // ──────────────────────────────────────────────
    // 字段相关性（Pearson / Spearman）
    // ──────────────────────────────────────────────

    [Fact]
    public void Correlation_TwoCorrelatedNumericFields_DetectsStrongPair()
    {
        // 构造 50 个对象：y = 2x + ε
        var rng = new Random(42);
        var objects = new List<Dictionary<string, object?>>();
        for (int k = 0; k < 50; k++)
        {
            double x = k;
            double y = 2 * x + rng.NextDouble();
            objects.Add(new() { ["x"] = x, ["y"] = y });
        }
        var features = new AdvancedAnalysisOptions { EnableCorrelation = true };
        var result = Analyze(objects, features: features);

        Assert.NotNull(result.CorrelationMatrix);
        var fields = result.CorrelationMatrix!.Fields;
        Assert.Contains("x", fields);
        Assert.Contains("y", fields);
        int i = fields.IndexOf("x");
        int j = fields.IndexOf("y");
        // x / y 应高度正相关
        var pearson = result.CorrelationMatrix.Pearson[i][j];
        Assert.NotNull(pearson);
        Assert.True(pearson!.Value > 0.9, $"期望 Pearson > 0.9，实际 {pearson.Value}");
        // Spearman 应同样高
        var spearman = result.CorrelationMatrix.Spearman[i][j];
        Assert.NotNull(spearman);
        Assert.True(spearman!.Value > 0.9, $"期望 Spearman > 0.9，实际 {spearman.Value}");
    }

    [Fact]
    public void Correlation_DisabledByDefault_ReturnsNull()
    {
        var objects = new List<Dictionary<string, object?>>
        {
            new() { ["a"] = 1.0, ["b"] = 2.0 },
            new() { ["a"] = 2.0, ["b"] = 4.0 },
            new() { ["a"] = 3.0, ["b"] = 6.0 }
        };
        var result = Analyze(objects);
        Assert.Null(result.CorrelationMatrix);
    }

    // ──────────────────────────────────────────────
    // 字符串模式归纳
    // ──────────────────────────────────────────────

    [Fact]
    public void StringPatterns_PhoneAndZip_InfersTwoTemplates()
    {
        var objects = new List<Dictionary<string, object?>>();
        for (int i = 0; i < 10; i++)
        {
            objects.Add(new() { ["contact"] = $"1380013{i:D4}" }); // 手机号
            objects.Add(new() { ["contact"] = $"10008{i}" });      // 邮编（5 位数字）
        }
        var features = new AdvancedAnalysisOptions { EnableStringPatterns = true };
        var result = Analyze(objects, features: features);

        Assert.NotNull(result.StringPatterns);
        var profile = result.StringPatterns!["contact"];
        Assert.Equal(20, profile.TotalSamples);
        // 应有多个模板；主模板是手机号
        Assert.NotNull(profile.DominantPattern);
        Assert.NotEmpty(profile.Patterns);
    }

    [Fact]
    public void StringPattern_BuildTemplate_HandlesDigitsAndLetters()
    {
        // 一对一替换：数字 → '#'、字母 → '?'、其它字面保留
        Assert.Equal("###-####", StringPatternProfiler.BuildTemplate("123-4567"));
        Assert.Equal("???-####", StringPatternProfiler.BuildTemplate("abc-1234"));
        Assert.Equal("##@##.###", StringPatternProfiler.BuildTemplate("12@34.567"));
        Assert.Equal("???-####-????", StringPatternProfiler.BuildTemplate("abc-1234-xyzw"));
    }

    // ──────────────────────────────────────────────
    // Unicode 卫生检查
    // ──────────────────────────────────────────────

    [Fact]
    public void UnicodeHygiene_FullHalfMixedAndInvisible_FlagsBoth()
    {
        var objects = new List<Dictionary<string, object?>>
        {
            new() { ["name"] = "Ａlice" },        // 全角 A
            new() { ["name"] = "Bob　" },        // 全角空格（尾随）
            new() { ["name"] = "张三" },
            new() { ["name"] = "Carol\u200B" },  // 零宽空格
        };
        var features = new AdvancedAnalysisOptions { EnableUnicodeHygiene = true };
        var result = Analyze(objects, features: features);

        Assert.NotNull(result.UnicodeHygiene);
        var r = result.UnicodeHygiene!["name"];
        Assert.True(r.FullwidthCount > 0, "应识别到全角字符");
        Assert.True(r.InvisibleCount > 0, "应识别到不可见字符");
        Assert.True(r.SurroundingWhitespaceCount > 0, "应识别到首尾空白");
    }

    // ──────────────────────────────────────────────
    // 时间序列深度分析
    // ──────────────────────────────────────────────

    [Fact]
    public void TimeSeries_RisingTrend_DetectedAsRising()
    {
        // 90 个时间桶（每天一个），每个时间戳重复 1~90 次制造"该时间桶内有 i+1 个样本"
        // 时间跨度 ≥ 90 天 → 7 天桶 → 13 个桶，远超 MinBucketsForAnalysis=6
        var baseTime = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var expanded = new List<Dictionary<string, object?>>();
        for (int i = 0; i < 90; i++)
        {
            var ts = baseTime.AddDays(i);
            for (int k = 0; k <= i; k++)
            {
                expanded.Add(new() { ["ts"] = ts.ToString("o") });
            }
        }

        var features = new AdvancedAnalysisOptions { EnableTimeSeries = true };
        var result = Analyze(expanded, features: features);

        Assert.NotNull(result.TimeSeriesAnalyses);
        Assert.True(result.TimeSeriesAnalyses!.ContainsKey("ts"),
            $"TimeSeriesAnalyses 应包含 ts，实际 keys: {string.Join(",", result.TimeSeriesAnalyses.Keys)}");
        var ts0 = result.TimeSeriesAnalyses["ts"];
        // 趋势：递增样本数 → 上升趋势
        Assert.True(ts0.Trend == "rising" || ts0.Trend == "stable",
            $"趋势应为 rising/stable，实际 {ts0.Trend}");
        Assert.True(ts0.BucketCount >= 6, $"桶数应 ≥ 6，实际 {ts0.BucketCount}");
    }

    [Fact]
    public void TimeSeriesAnalyzer_LinearRegression_PerfectLineGivesRSquared1()
    {
        // 完全线性 y = x → R² = 1, slope = 1
        var y = Enumerable.Range(0, 20).Select(i => (double)i).ToArray();
        var (slope, r2) = TimeSeriesAnalyzer.LinearRegression(y);
        Assert.Equal(1.0, slope, 5);
        Assert.Equal(1.0, r2, 5);
    }

    // ──────────────────────────────────────────────
    // 模糊去重
    // ──────────────────────────────────────────────

    [Fact]
    public void FuzzyDedup_DifferentCasingAndWhitespace_GroupsTogether()
    {
        var objects = new List<Dictionary<string, object?>>
        {
            new() { ["name"] = "Alice" },
            new() { ["name"] = "alice" },
            new() { ["name"] = " ALICE " },
            new() { ["name"] = "Bob" },
            new() { ["name"] = "bob" },
        };
        var report = FuzzyDuplicateDetector.Detect(objects, o => o["name"]?.ToString());

        Assert.Equal(5, report.TotalSamples);
        // 至少 2 个组（Alice 与 Bob 各自）
        Assert.True(report.GroupCount >= 2, $"GroupCount={report.GroupCount}");
        Assert.True(report.SimilarDuplicates >= 4, $"SimilarDuplicates={report.SimilarDuplicates}");
    }

    [Fact]
    public void FuzzyDedup_Canonicalize_FoldsFullAndHalfWidth()
    {
        // 全角 "Ａlice" 与半角 "Alice" 归一后相同
        Assert.Equal(FuzzyDuplicateDetector.Canonicalize("Alice"),
            FuzzyDuplicateDetector.Canonicalize("ＡＬＩＣＥ"));
    }

    // ──────────────────────────────────────────────
    // 分布快照 / diff
    // ──────────────────────────────────────────────

    [Fact]
    public void DistributionSnapshot_RoundTrip_PreservesMeanAndStdDev()
    {
        var objects = new List<Dictionary<string, object?>>();
        for (int i = 0; i < 100; i++)
        {
            objects.Add(new() { ["x"] = (double)i });
        }
        var (result, quantiles, distincts) = AnalyzeDetailed(objects);
        var snapshot = DistributionSnapshotExporter.Build(result, quantiles, distincts, "test");
        Assert.Single(snapshot.Fields);
        var f = snapshot.Fields[0];
        Assert.Equal("x", f.Path);
        Assert.Equal("Number", f.PrimaryType);
        Assert.Equal(100, f.Count);
        Assert.NotNull(f.MeanNumber);
        Assert.InRange(f.MeanNumber!.Value, 49, 50);
    }

    [Fact]
    public void DistributionDiffer_MeanShift_ReportsNumericDrift()
    {
        // baseline: mean ≈ 50
        var baseline = new List<Dictionary<string, object?>>();
        for (int i = 0; i < 100; i++) baseline.Add(new() { ["x"] = (double)i });

        // current: mean ≈ 60
        var current = new List<Dictionary<string, object?>>();
        for (int i = 0; i < 100; i++) current.Add(new() { ["x"] = (double)(i + 10) });

        var (bRes, bQ, bD) = AnalyzeDetailed(baseline);
        var (cRes, cQ, cD) = AnalyzeDetailed(current);

        var bSnap = DistributionSnapshotExporter.Build(bRes, bQ, bD, "b");
        var cSnap = DistributionSnapshotExporter.Build(cRes, cQ, cD, "c");

        var diff = DistributionDiffer.Compute(bSnap, cSnap);
        Assert.NotEmpty(diff.NumericDrifts);
        var drift = diff.NumericDrifts.First();
        Assert.Equal("x", drift.Path);
        Assert.True(Math.Abs(drift.MeanShift ?? 0) > 0, "均值应发生变化");
    }

    // ──────────────────────────────────────────────
    // 采样置信区间
    // ──────────────────────────────────────────────

    [Fact]
    public void SamplingConfidence_HighCoverage_NotLowConfidence()
    {
        var sc = SamplingConfidenceCalculator.Compute(observed: 0.95, sampleSize: 200, confidenceLevel: 0.95);
        Assert.False(sc.Ambiguous);
        Assert.True(sc.Width < 0.1, $"覆盖 95% / n=200 区间应很窄，实际 width={sc.Width}");
    }

    [Fact]
    public void SamplingConfidence_ZeroObserved_MayBeAmbiguous()
    {
        // n=0 触发 sampleSize<=0 早返回路径 → Ambiguous=true（"完全没样本"含义模糊）
        var sc = SamplingConfidenceCalculator.Compute(observed: 0.0, sampleSize: 0);
        Assert.True(sc.Ambiguous, "n=0 时应判定为 Ambiguous");
    }

    [Fact]
    public void SamplingConfidence_Build_ProducesSummaryAndLowConfidenceFields()
    {
        // 总样本 20：hot=20(100%) 窄区间，cold=1(5%) 宽区间 → 触发 LowConfidenceFields
        var fieldCounts = new List<(string, long)>
        {
            ("hot", 20),  // 100% → 窄区间（width ≈ 0.16 < 0.2）
            ("cold", 1),  // 5%  → 宽区间（width ≈ 0.23 ≥ 0.2）
        };
        var sc = SamplingConfidenceCalculator.Build("reservoir", 20, 20, fieldCounts, 20);
        Assert.Equal(20, sc.EffectiveSampleSize);
        Assert.Equal(2, sc.CoverageIntervals.Count);
        Assert.Contains("cold", sc.LowConfidenceFields);
        Assert.DoesNotContain("hot", sc.LowConfidenceFields);
        Assert.Contains("采样方式: reservoir", sc.Summary);
    }

    // ──────────────────────────────────────────────
    // AnalysisRunner 端到端（CLI 集成）
    // ──────────────────────────────────────────────

    [Fact]
    public void AnalysisRunner_CorrelationFlag_BuildsAndWritesReport()
    {
        string inputPath = Path.Combine(Path.GetTempPath(), $"p4-corr-{Guid.NewGuid():N}.json");
        string outputPath = Path.Combine(Path.GetTempPath(), $"p4-corr-{Guid.NewGuid():N}.json");

        try
        {
            // 写 50 个对象，a / b 强正相关（≥30 才会被列入 StrongPairs）
            var objs = Enumerable.Range(0, 50)
                .Select(i => new Dictionary<string, object?> { ["a"] = i, ["b"] = i * 2 })
                .ToList();
            File.WriteAllText(inputPath, JsonSerializer.Serialize(objs));

            var options = new AnalysisOptions
            {
                Input = { InputPath = inputPath },
                Report = { OutputPath = outputPath },
                Advanced = { EnableCorrelation = true },
            };
            int code = AnalysisRunner.Run(options);
            Assert.Equal(0, code);

            // 读回 JSON 报告，应包含 correlationMatrix
            var report = JsonSerializer.Deserialize<AnalysisResult>(File.ReadAllText(outputPath));
            Assert.NotNull(report);
            Assert.NotNull(report!.CorrelationMatrix);
            Assert.Contains("a", report.CorrelationMatrix!.Fields);
            Assert.Contains("b", report.CorrelationMatrix.Fields);
            int ia = report.CorrelationMatrix.Fields.IndexOf("a");
            int ib = report.CorrelationMatrix.Fields.IndexOf("b");
            var pearson = report.CorrelationMatrix.Pearson[ia][ib];
            Assert.NotNull(pearson);
            Assert.True(pearson!.Value > 0.99, $"a/b 完全线性相关，期望 Pearson>0.99，实际 {pearson}");
        }
        finally
        {
            if (File.Exists(inputPath)) File.Delete(inputPath);
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
    }

    [Fact]
    public void AnalysisRunner_FuzzyDedupField_PopulatesFuzzyDuplicates()
    {
        string inputPath = Path.Combine(Path.GetTempPath(), $"p4-fuzz-{Guid.NewGuid():N}.json");
        string outputPath = Path.Combine(Path.GetTempPath(), $"p4-fuzz-{Guid.NewGuid():N}.json");

        try
        {
            // 两组形态变体：Alice(3) + Bob(3)；期望 GroupCount >= 2
            var objs = new List<Dictionary<string, object?>>
            {
                new() { ["name"] = "Alice" },
                new() { ["name"] = "alice" },
                new() { ["name"] = " ALICE " },
                new() { ["name"] = "Bob" },
                new() { ["name"] = "bob" },
                new() { ["name"] = "BOB" },
            };
            File.WriteAllText(inputPath, JsonSerializer.Serialize(objs));

            var options = new AnalysisOptions
            {
                Input = { InputPath = inputPath },
                Report = { OutputPath = outputPath },
                Advanced = { FuzzyDedupField = "name" },
            };
            int code = AnalysisRunner.Run(options);
            Assert.Equal(0, code);

            string json = File.ReadAllText(outputPath);
            bool hasFuzzy = json.Contains("fuzzyDuplicates", StringComparison.OrdinalIgnoreCase);
            if (!hasFuzzy) throw new Xunit.Sdk.XunitException("JSON 中未包含 fuzzyDuplicates 字段");

            var report = JsonSerializer.Deserialize<AnalysisResult>(File.ReadAllText(outputPath));
            Assert.NotNull(report!.FuzzyDuplicates);
            Assert.Equal(6, report.FuzzyDuplicates!.TotalSamples);
            Assert.True(report.FuzzyDuplicates.GroupCount >= 2,
                $"期望至少 2 个相似重复组（Alice + Bob），实际 {report.FuzzyDuplicates.GroupCount}");
            Assert.True(report.FuzzyDuplicates.SimilarDuplicates >= 6,
                $"期望 6 个相似重复样本，实际 {report.FuzzyDuplicates.SimilarDuplicates}");
        }
        finally
        {
            if (File.Exists(inputPath)) File.Delete(inputPath);
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
    }

    [Fact]
    public void AnalysisRunner_DistributionSnapshot_WritesSnapshotFile()
    {
        string inputPath = Path.Combine(Path.GetTempPath(), $"p4-snap-{Guid.NewGuid():N}.json");
        string snapPath = Path.Combine(Path.GetTempPath(), $"p4-snap-{Guid.NewGuid():N}.json");
        string outputPath = Path.Combine(Path.GetTempPath(), $"p4-out-{Guid.NewGuid():N}.json");

        try
        {
            var objs = Enumerable.Range(0, 50)
                .Select(i => new Dictionary<string, object?> { ["x"] = (double)i })
                .ToList();
            File.WriteAllText(inputPath, JsonSerializer.Serialize(objs));

            var options = new AnalysisOptions
            {
                Input = { InputPath = inputPath },
                Report = { OutputPath = outputPath },
                Distribution = { DistributionSnapshotPath = snapPath },
            };
            int code = AnalysisRunner.Run(options);
            Assert.Equal(0, code);

            Assert.True(File.Exists(snapPath), "分布快照应已导出");
            var snap = JsonSerializer.Deserialize<DistributionSnapshot>(File.ReadAllText(snapPath));
            Assert.NotNull(snap);
            Assert.NotEmpty(snap!.Fields);
        }
        finally
        {
            if (File.Exists(inputPath)) File.Delete(inputPath);
            if (File.Exists(snapPath)) File.Delete(snapPath);
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
    }

    [Fact]
    public void AnalysisRunner_SamplingConfidence_PopulatedWhenSamplingEnabled()
    {
        string inputPath = Path.Combine(Path.GetTempPath(), $"p4-sample-{Guid.NewGuid():N}.jsonl");
        string outputPath = Path.Combine(Path.GetTempPath(), $"p4-sample-{Guid.NewGuid():N}.json");

        try
        {
            // 写 200 行 JSONL
            using (var sw = new StreamWriter(inputPath))
            {
                for (int i = 0; i < 200; i++)
                {
                    sw.WriteLine(JsonSerializer.Serialize(new { id = i, val = i % 10 }));
                }
            }

            var options = new AnalysisOptions
            {
                Input = { InputPath = inputPath },
                Report = { OutputPath = outputPath },
                Sampling = { SampleReservoir = 50 },
            };
            int code = AnalysisRunner.Run(options);
            Assert.Equal(0, code);

            var report = JsonSerializer.Deserialize<AnalysisResult>(File.ReadAllText(outputPath));
            Assert.NotNull(report!.SamplingConfidence);
            Assert.Equal("reservoir", report.SamplingConfidence!.SamplingMethod);
            Assert.Equal(50, report.SamplingConfidence.EffectiveSampleSize);
        }
        finally
        {
            if (File.Exists(inputPath)) File.Delete(inputPath);
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
    }
}
