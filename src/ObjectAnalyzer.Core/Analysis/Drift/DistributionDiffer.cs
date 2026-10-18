
namespace ObjectAnalyzer.Analysis;

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
        if (baseline.SnapshotVersion != "2.0" || current.SnapshotVersion != "2.0")
            throw new InvalidDataException("Snapshot version 2.0 is required. Re-analyze legacy sources to rebuild typed value identities and field paths.");

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
    public static void PrintSummary(DistributionDiff diff, int maxPerCategory = 10, IAnalysisConsole? console = null)
    {
        if (diff.SignificantDriftCount == 0 && diff.AddedFields.Count == 0 && diff.RemovedFields.Count == 0)
        {
            (console ?? SilentConsole.Instance).MarkupLine("[green]✓ 无显著分布漂移（与基线一致）。[/]");
            return;
        }

        (console ?? SilentConsole.Instance).MarkupLine(
            $"[bold]分布漂移[/] — 显著变化 {diff.SignificantDriftCount} 处 / 覆盖率变化 {diff.CoverageChanges.Count} / 数值漂移 {diff.NumericDrifts.Count} / distinct 漂移 {diff.DistinctDrifts.Count}");

        if (diff.AddedFields.Count > 0)
        {
            (console ?? SilentConsole.Instance).MarkupLine($"[green]+ 新出现字段[/] [grey]({diff.AddedFields.Count})[/]");
            foreach (var f in diff.AddedFields.Take(maxPerCategory))
            {
                (console ?? SilentConsole.Instance).MarkupLine($"  [green]+[/] [cyan]{MarkupText.Escape(f.Path)}[/] [grey]({MarkupText.Escape(f.PrimaryType)}, coverage {f.Coverage:P1})[/]");
            }
        }
        if (diff.RemovedFields.Count > 0)
        {
            (console ?? SilentConsole.Instance).MarkupLine($"[red]- 消失字段[/] [grey]({diff.RemovedFields.Count})[/]");
            foreach (var f in diff.RemovedFields.Take(maxPerCategory))
            {
                (console ?? SilentConsole.Instance).MarkupLine($"  [red]-[/] [cyan]{MarkupText.Escape(f.Path)}[/]");
            }
        }

        if (diff.NumericDrifts.Count > 0)
        {
            (console ?? SilentConsole.Instance).MarkupLine($"[yellow]~ 数值漂移[/] [grey]({diff.NumericDrifts.Count})[/]");
            foreach (var n in diff.NumericDrifts.Take(maxPerCategory).Where(n => Math.Abs(n.MeanShiftSigma ?? 0) >= MeanShiftSigmaThreshold))
            {
                string tag = (n.MeanShiftSigma is > 0.5 or < -0.5) ? "[red]✗[/]" : "[yellow]~[/]";
                (console ?? SilentConsole.Instance).MarkupLine(
                    $"  {tag} [cyan]{MarkupText.Escape(n.Path)}[/]: μ {n.BaselineMean:G4} → {n.CurrentMean:G4} (Δ {(n.MeanShiftSigma ?? 0):+0.00;-0.00;0.00}σ)");
            }
        }

        if (diff.DistinctDrifts.Count > 0)
        {
            (console ?? SilentConsole.Instance).MarkupLine($"[yellow]~ distinct 漂移[/] [grey]({diff.DistinctDrifts.Count})[/]");
            foreach (var d in diff.DistinctDrifts.Where(d => d.Severity != "info").Take(maxPerCategory))
            {
                string tag = d.Severity == "high" ? "[red]✗[/]" : "[yellow]~[/]";
                string jaccard = d.Jaccard.HasValue ? $"Jaccard={d.Jaccard:P1}" : "";
                (console ?? SilentConsole.Instance).MarkupLine(
                    $"  {tag} [cyan]{MarkupText.Escape(d.Path)}[/]: distinct {d.BaselineDistinct} → {d.CurrentDistinct} {MarkupText.Escape(jaccard)}");
            }
        }
    }
}
