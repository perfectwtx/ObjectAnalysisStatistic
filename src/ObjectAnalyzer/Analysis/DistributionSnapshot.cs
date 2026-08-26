using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;

namespace ObjectAnalyzer.Analysis;

/// <summary>
/// 分布快照：每字段保留足以做"分布漂移"对比的草图与摘要。
/// 设计目标：
///  - JSON 可序列化：t-digest 用质心数组、HLL 用稠密寄存器的字节序列
///  - 内存有界：每字段 ~10KB 以内（256 质心 + 16K 寄存器）
///  - 与 SchemaSnapshot 互补：SchemaSnapshot 描述"结构"，DistributionSnapshot 描述"分布"
/// 适用场景：
///  - null 率从 1% 升到 20%（用 NullRateBefore/After 直接可见）
///  - 均值偏移 1σ（用 MeanBefore/After + StdDev 对比）
///  - 枚举字段出现新值（用 HyperLogLog 估计 distinct count + Jaccard 对比）
///  - 数值分布整体迁移（用 t-digest 多分位对比）
/// </summary>
public sealed class DistributionSnapshot
{
    /// <summary>快照格式版本。</summary>
    [JsonPropertyName("snapshotVersion")]
    public string SnapshotVersion { get; set; } = "1.0";

    /// <summary>生成时间（UTC）。</summary>
    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>来源标识（输入文件路径 / db://provider?query）。</summary>
    [JsonPropertyName("source")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Source { get; set; }

    /// <summary>被分析对象总数。</summary>
    [JsonPropertyName("totalObjects")]
    public long TotalObjects { get; set; }

    /// <summary>字段分布列表。</summary>
    [JsonPropertyName("fields")]
    public List<DistributionSnapshotField> Fields { get; set; } = new();
}

/// <summary>单字段分布快照。</summary>
public sealed class DistributionSnapshotField
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";

    /// <summary>主导类型（Number / String / DateTime / Boolean）。</summary>
    [JsonPropertyName("primaryType")] public string PrimaryType { get; set; } = "Unknown";

    /// <summary>覆盖率（字段在对象中出现的比例）。</summary>
    [JsonPropertyName("coverage")] public double Coverage { get; set; }

    /// <summary>出现次数（Count，非样本数）。</summary>
    [JsonPropertyName("count")] public long Count { get; set; }

    // ── 数值字段 ──
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? MeanNumber { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? StdDevNumber { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? MinNumber { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? MaxNumber { get; set; }

    /// <summary>t-digest 质心序列：[(mean, weight), ...]。可重建 t-digest 用于多分位对比。</summary>
    [JsonPropertyName("quantileCentroids")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<QuantileCentroid>? QuantileCentroids { get; set; }

    // ── 字符串字段 ──
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MinLength { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxLength { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? AvgLength { get; set; }

    /// <summary>HyperLogLog 稠密寄存器（base64 编码）；可重建 HLL 用于 distinct 对比。</summary>
    [JsonPropertyName("distinctHllRegisters")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DistinctHllRegisters { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DistinctHllPrecision { get; set; }

    /// <summary>HyperLogLog 基数估计（快照时记录，避免重建）。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? DistinctCount { get; set; }
}

/// <summary>t-digest 单个质心（均值 + 权重）。</summary>
public sealed class QuantileCentroid
{
    [JsonPropertyName("mean")] public double Mean { get; set; }
    [JsonPropertyName("weight")] public long Weight { get; set; }
}

/// <summary>
/// 分布差异：对比两份 <see cref="DistributionSnapshot"/>，
/// 分类：null 率变化、均值偏移、分位迁移、distinct 漂移。
/// </summary>
public sealed class DistributionDiff
{
    [JsonPropertyName("diffVersion")] public string DiffVersion { get; set; } = "1.0";
    [JsonPropertyName("generatedAt")] public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;
    [JsonPropertyName("baselineSource")] public string? BaselineSource { get; set; }
    [JsonPropertyName("currentSource")] public string? CurrentSource { get; set; }

    /// <summary>覆盖率变化列表（基线 - 当前，按 |Δcoverage| 降序）。</summary>
    [JsonPropertyName("coverageChanges")]
    public List<DistributionFieldChange> CoverageChanges { get; set; } = new();

    /// <summary>数值分布漂移：均值 / 中位数 / P95 偏移列表。</summary>
    [JsonPropertyName("numericDrifts")]
    public List<NumericDrift> NumericDrifts { get; set; } = new();

    /// <summary>distinct 漂移：基线 vs 当前的不同值集 Jaccard + 计数变化。</summary>
    [JsonPropertyName("distinctDrifts")]
    public List<DistinctDrift> DistinctDrifts { get; set; } = new();

    /// <summary>新出现的字段 / 已消失字段（与 SchemaDiff 类似但口径仅看"出现过的字段"）。</summary>
    [JsonPropertyName("addedFields")]
    public List<DistributionSnapshotField> AddedFields { get; set; } = new();
    [JsonPropertyName("removedFields")]
    public List<DistributionSnapshotField> RemovedFields { get; set; } = new();

    /// <summary>显著漂移的字段总数（coverage Δ ≥ 0.1 或 |mean shift| ≥ 0.5σ）。</summary>
    [JsonPropertyName("significantDriftCount")]
    public int SignificantDriftCount { get; set; }
}

/// <summary>覆盖率变化（baseline - current 视为变化量；正数表示覆盖率下降）。</summary>
public sealed class DistributionFieldChange
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("baselineCoverage")] public double BaselineCoverage { get; set; }
    [JsonPropertyName("currentCoverage")] public double CurrentCoverage { get; set; }
    [JsonPropertyName("delta")] public double Delta { get; set; }
}

/// <summary>数值字段漂移：均值 / 分位 / 标准差偏移。</summary>
public sealed class NumericDrift
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? BaselineMean { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? CurrentMean { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? MeanShift { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? MeanShiftSigma { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? BaselineMedian { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? CurrentMedian { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? MedianShift { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? BaselineP95 { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? CurrentP95 { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? P95Shift { get; set; }
}

/// <summary>distinct 漂移：Jaccard（值集合相似度）+ 计数变化。</summary>
public sealed class DistinctDrift
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? BaselineDistinct { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? CurrentDistinct { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? DeltaRatio { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Jaccard { get; set; }

    [JsonPropertyName("severity")]
    public string Severity { get; set; } = "info";
}

/// <summary>
/// 分布快照导出器：从 <see cref="AnalysisResult"/> 提取 t-digest / HLL / 摘要，输出 JSON。
/// </summary>
public static class DistributionSnapshotExporter
{
    private static readonly JsonSerializerOptions s_writeOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions s_loadOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>从分析结果与流式度量构建分布快照。</summary>
    public static DistributionSnapshot Build(
        AnalysisResult result,
        Dictionary<string, MergeableQuantileSketch>? quantileSketches,
        Dictionary<string, HyperLogLog>? distinctSketches,
        string? source = null)
    {
        var snap = new DistributionSnapshot
        {
            Source = source,
            TotalObjects = result.TotalObjects,
            CreatedAt = DateTimeOffset.UtcNow
        };
        foreach (var stat in result.FieldStatistics)
        {
            var field = new DistributionSnapshotField
            {
                Path = stat.FieldName,
                PrimaryType = stat.PrimaryType,
                Coverage = stat.Coverage,
                Count = stat.Count
            };
            if (stat.PrimaryType == "Number")
            {
                field.MeanNumber = stat.AvgNumber;
                field.StdDevNumber = stat.StdDevNumber;
                field.MinNumber = stat.MinNumber;
                field.MaxNumber = stat.MaxNumber;
                if (quantileSketches != null && quantileSketches.TryGetValue(stat.FieldName, out var q) && q.TotalWeight > 0)
                {
                    field.QuantileCentroids = QuantileCentroidExtractor.Extract(q);
                }
            }
            if (stat.PrimaryType == "String" || stat.PrimaryType == "DateTime")
            {
                field.MinLength = stat.MinLength;
                field.MaxLength = stat.MaxLength;
                field.AvgLength = stat.AvgLength;
            }
            if (distinctSketches != null && distinctSketches.TryGetValue(stat.FieldName, out var hll))
            {
                field.DistinctHllRegisters = HllSerializer.ToBase64(hll);
                field.DistinctHllPrecision = HllSerializer.GetPrecision(hll);
                field.DistinctCount = hll.Estimate();
            }
            else if (stat.DistinctCount.HasValue)
            {
                field.DistinctCount = stat.DistinctCount;
            }
            snap.Fields.Add(field);
        }
        return snap;
    }

    public static void WriteToFile(DistributionSnapshot snapshot, string path)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(snapshot, s_writeOptions), new UTF8Encoding(false));
    }

    public static DistributionSnapshot LoadFromFile(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"分布快照文件不存在: {path}", path);
        var json = File.ReadAllText(path, Encoding.UTF8);
        var snap = JsonSerializer.Deserialize<DistributionSnapshot>(json, s_loadOptions);
        if (snap == null) throw new InvalidDataException($"分布快照解析失败: {path}");
        return snap;
    }
}

/// <summary>从 <see cref="MergeableQuantileSketch"/> 提取质心序列（公开便于测试）。</summary>
public static class QuantileCentroidExtractor
{
    public static List<QuantileCentroid>? Extract(MergeableQuantileSketch sketch)
    {
        if (sketch == null) return null;
        // 通过反射读取私有 _centroids 字段（简单且不暴露内部 API）。
        var field = typeof(MergeableQuantileSketch).GetField("_centroids",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (field == null) return null;
        var centroids = field.GetValue(sketch) as System.Collections.IEnumerable;
        if (centroids == null) return null;

        var list = new List<QuantileCentroid>();
        foreach (var c in centroids)
        {
            // c 是 (double Mean, long Weight) 的 record struct
            var t = c.GetType();
            var mean = (double)t.GetProperty("Mean")!.GetValue(c)!;
            var weight = (long)t.GetProperty("Weight")!.GetValue(c)!;
            list.Add(new QuantileCentroid { Mean = mean, Weight = weight });
        }
        return list;
    }

    /// <summary>从质心数组重建 t-digest 草图（用于分布对比）。</summary>
    public static MergeableQuantileSketch Rehydrate(List<QuantileCentroid> centroids, int capacity = 256)
    {
        var sketch = new MergeableQuantileSketch(capacity);
        foreach (var c in centroids)
        {
            // 不区分 double/long，直接 Add
            sketch.Add(c.Mean, c.Weight);
        }
        return sketch;
    }
}

/// <summary>HyperLogLog 序列化辅助：稠密寄存器 → base64，便于 JSON 存储。</summary>
public static class HllSerializer
{
    public static string? ToBase64(HyperLogLog hll)
    {
        var dense = GetDenseRegisters(hll);
        if (dense == null) return null;
        return Convert.ToBase64String(dense);
    }

    public static int? GetPrecision(HyperLogLog hll)
    {
        var dense = GetDenseRegisters(hll);
        if (dense == null) return null;
        return (int)Math.Log2(dense.Length);
    }

    public static HyperLogLog FromBase64(string base64, int precision)
    {
        var bytes = Convert.FromBase64String(base64);
        var hll = new HyperLogLog(precision);
        SetDenseRegisters(hll, bytes);
        return hll;
    }

    private static byte[]? GetDenseRegisters(HyperLogLog hll)
    {
        var field = typeof(HyperLogLog).GetField("_dense",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        return field?.GetValue(hll) as byte[];
    }

    private static void SetDenseRegisters(HyperLogLog hll, byte[] bytes)
    {
        var field = typeof(HyperLogLog).GetField("_dense",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        field?.SetValue(hll, bytes);
    }

    /// <summary>两个 HLL 之间的 Jaccard 相似度估算（基于寄存器的位级 Jaccard）。</summary>
    public static double? JaccardApprox(HyperLogLog a, HyperLogLog b)
    {
        var ra = GetDenseRegisters(a);
        var rb = GetDenseRegisters(b);
        if (ra == null || rb == null || ra.Length != rb.Length) return null;
        int inter = 0, union = 0;
        for (int i = 0; i < ra.Length; i++)
        {
            byte max = Math.Max(ra[i], rb[i]);
            byte min = Math.Min(ra[i], rb[i]);
            if (max > 0) union++;
            if (min > 0) inter++;
        }
        return union == 0 ? 1.0 : (double)inter / union;
    }
}

/// <summary>
/// 分布差异计算器：两份 <see cref="DistributionSnapshot"/> 之间的对比。
/// </summary>
public static class DistributionDiffer
{
    /// <summary>显著漂移阈值：mean shift ≥ 0.5σ 视为显著。</summary>
    public const double MeanShiftSigmaThreshold = 0.5;

    /// <summary>显著覆盖率变化阈值（绝对值）。</summary>
    public const double CoverageDeltaThreshold = 0.1;

    public static DistributionDiff Compute(DistributionSnapshot baseline, DistributionSnapshot current)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(current);

        var diff = new DistributionDiff
        {
            BaselineSource = baseline.Source,
            CurrentSource = current.Source
        };

        var byBase = baseline.Fields.ToDictionary(f => f.Path, StringComparer.Ordinal);
        var byCur = current.Fields.ToDictionary(f => f.Path, StringComparer.Ordinal);

        // 1) 覆盖率变化
        foreach (var (path, cur) in byCur)
        {
            double baseCov = byBase.TryGetValue(path, out var b) ? b.Coverage : 0.0;
            double delta = baseCov - cur.Coverage;
            if (Math.Abs(delta) >= 0.01)
            {
                diff.CoverageChanges.Add(new DistributionFieldChange
                {
                    Path = path,
                    BaselineCoverage = baseCov,
                    CurrentCoverage = cur.Coverage,
                    Delta = delta
                });
            }
        }
        diff.CoverageChanges = diff.CoverageChanges
            .OrderByDescending(c => Math.Abs(c.Delta))
            .ToList();

        // 2) 新增 / 消失字段
        foreach (var (path, b) in byBase)
        {
            if (!byCur.ContainsKey(path))
                diff.RemovedFields.Add(b);
        }
        foreach (var (path, c) in byCur)
        {
            if (!byBase.ContainsKey(path))
                diff.AddedFields.Add(c);
        }

        // 3) 数值漂移
        foreach (var (path, cur) in byCur)
        {
            if (cur.PrimaryType != "Number" || !byBase.TryGetValue(path, out var b)) continue;
            if (b.PrimaryType != "Number") continue;

            var nd = new NumericDrift { Path = path };
            nd.BaselineMean = b.MeanNumber;
            nd.CurrentMean = cur.MeanNumber;
            if (b.MeanNumber.HasValue && cur.MeanNumber.HasValue)
            {
                nd.MeanShift = cur.MeanNumber.Value - b.MeanNumber.Value;
                if (b.StdDevNumber is > 0)
                {
                    nd.MeanShiftSigma = nd.MeanShift / b.StdDevNumber.Value;
                }
            }

            // 分位迁移：基线与当前 t-digest 重建
            if (b.QuantileCentroids != null && cur.QuantileCentroids != null)
            {
                var baseSketch = QuantileCentroidExtractor.Rehydrate(b.QuantileCentroids);
                var curSketch = QuantileCentroidExtractor.Rehydrate(cur.QuantileCentroids);
                nd.BaselineMedian = baseSketch.Quantile(0.50);
                nd.CurrentMedian = curSketch.Quantile(0.50);
                if (nd.BaselineMedian.HasValue && nd.CurrentMedian.HasValue)
                {
                    nd.MedianShift = nd.CurrentMedian.Value - nd.BaselineMedian.Value;
                }
                nd.BaselineP95 = baseSketch.Quantile(0.95);
                nd.CurrentP95 = curSketch.Quantile(0.95);
                if (nd.BaselineP95.HasValue && nd.CurrentP95.HasValue)
                {
                    nd.P95Shift = nd.CurrentP95.Value - nd.BaselineP95.Value;
                }
            }
            else
            {
                // 无 t-digest：尝试用 MedianNumber / P95Number 字段（如果存在）
                nd.BaselineMedian = b.QuantileCentroids != null
                    ? QuantileCentroidExtractor.Rehydrate(b.QuantileCentroids).Quantile(0.50)
                    : null;
                nd.CurrentMedian = cur.QuantileCentroids != null
                    ? QuantileCentroidExtractor.Rehydrate(cur.QuantileCentroids).Quantile(0.50)
                    : null;
            }
            diff.NumericDrifts.Add(nd);
        }
        diff.NumericDrifts = diff.NumericDrifts
            .Where(n => n.MeanShift.HasValue || n.MedianShift.HasValue || n.P95Shift.HasValue)
            .OrderByDescending(n => Math.Abs(n.MeanShiftSigma ?? 0))
            .ToList();

        // 4) distinct 漂移（HLL Jaccard 估计）
        foreach (var (path, cur) in byCur)
        {
            if (!byBase.TryGetValue(path, out var b)) continue;
            var dd = new DistinctDrift { Path = path };
            dd.BaselineDistinct = b.DistinctCount;
            dd.CurrentDistinct = cur.DistinctCount;
            if (b.DistinctCount is > 0 && cur.DistinctCount is > 0)
            {
                dd.DeltaRatio = (double)(cur.DistinctCount.Value - b.DistinctCount.Value) / b.DistinctCount.Value;
            }
            if (!string.IsNullOrEmpty(b.DistinctHllRegisters) && !string.IsNullOrEmpty(cur.DistinctHllRegisters)
                && b.DistinctHllPrecision is int bp && cur.DistinctHllPrecision is int cp && bp == cp)
            {
                var bh = HllSerializer.FromBase64(b.DistinctHllRegisters, bp);
                var ch = HllSerializer.FromBase64(cur.DistinctHllRegisters, cp);
                dd.Jaccard = HllSerializer.JaccardApprox(bh, ch);
            }
            // 严重性：Jaccard < 0.7 或 distinct 变化 > 30%
            if (dd.Jaccard is < 0.7)
                dd.Severity = "high";
            else if (dd.Jaccard is < 0.9 || Math.Abs(dd.DeltaRatio ?? 0) > 0.3)
                dd.Severity = "medium";
            else
                dd.Severity = "info";
            diff.DistinctDrifts.Add(dd);
        }
        diff.DistinctDrifts = diff.DistinctDrifts
            .OrderBy(d => d.Severity == "high" ? 0 : d.Severity == "medium" ? 1 : 2)
            .ThenByDescending(d => Math.Abs(d.DeltaRatio ?? 0))
            .ToList();

        // 5) 显著漂移计数
        int sigCov = diff.CoverageChanges.Count(c => Math.Abs(c.Delta) >= CoverageDeltaThreshold);
        int sigMean = diff.NumericDrifts.Count(n => Math.Abs(n.MeanShiftSigma ?? 0) >= MeanShiftSigmaThreshold);
        int sigDistinct = diff.DistinctDrifts.Count(d => d.Severity == "high");
        diff.SignificantDriftCount = sigCov + sigMean + sigDistinct;

        return diff;
    }

    /// <summary>控制台打印 diff 摘要。</summary>
    public static void PrintSummary(DistributionDiff diff, int maxPerCategory = 10)
    {
        if (diff.SignificantDriftCount == 0 && diff.AddedFields.Count == 0 && diff.RemovedFields.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]✓ 无显著分布漂移（与基线一致）。[/]");
            return;
        }

        AnsiConsole.MarkupLine(
            $"[bold]分布漂移[/] — 显著变化 {diff.SignificantDriftCount} 处 / 覆盖率变化 {diff.CoverageChanges.Count} / 数值漂移 {diff.NumericDrifts.Count} / distinct 漂移 {diff.DistinctDrifts.Count}");

        if (diff.AddedFields.Count > 0)
        {
            AnsiConsole.MarkupLine($"[green]+ 新出现字段[/] [grey]({diff.AddedFields.Count})[/]");
            foreach (var f in diff.AddedFields.Take(maxPerCategory))
            {
                AnsiConsole.MarkupLine($"  [green]+[/] [cyan]{Markup.Escape(f.Path)}[/] [grey]({Markup.Escape(f.PrimaryType)}, coverage {f.Coverage:P1})[/]");
            }
        }
        if (diff.RemovedFields.Count > 0)
        {
            AnsiConsole.MarkupLine($"[red]- 消失字段[/] [grey]({diff.RemovedFields.Count})[/]");
            foreach (var f in diff.RemovedFields.Take(maxPerCategory))
            {
                AnsiConsole.MarkupLine($"  [red]-[/] [cyan]{Markup.Escape(f.Path)}[/]");
            }
        }

        if (diff.NumericDrifts.Count > 0)
        {
            AnsiConsole.MarkupLine($"[yellow]~ 数值漂移[/] [grey]({diff.NumericDrifts.Count})[/]");
            foreach (var n in diff.NumericDrifts.Take(maxPerCategory).Where(n => Math.Abs(n.MeanShiftSigma ?? 0) >= MeanShiftSigmaThreshold))
            {
                string tag = (n.MeanShiftSigma is > 0.5 or < -0.5) ? "[red]✗[/]" : "[yellow]~[/]";
                AnsiConsole.MarkupLine(
                    $"  {tag} [cyan]{Markup.Escape(n.Path)}[/]: μ {n.BaselineMean:G4} → {n.CurrentMean:G4} (Δ {(n.MeanShiftSigma ?? 0):+0.00;-0.00;0.00}σ)");
            }
        }

        if (diff.DistinctDrifts.Count > 0)
        {
            AnsiConsole.MarkupLine($"[yellow]~ distinct 漂移[/] [grey]({diff.DistinctDrifts.Count})[/]");
            foreach (var d in diff.DistinctDrifts.Where(d => d.Severity != "info").Take(maxPerCategory))
            {
                string tag = d.Severity == "high" ? "[red]✗[/]" : "[yellow]~[/]";
                string jaccard = d.Jaccard.HasValue ? $"Jaccard={d.Jaccard:P1}" : "";
                AnsiConsole.MarkupLine(
                    $"  {tag} [cyan]{Markup.Escape(d.Path)}[/]: distinct {d.BaselineDistinct} → {d.CurrentDistinct} {Markup.Escape(jaccard)}");
            }
        }
    }
}
