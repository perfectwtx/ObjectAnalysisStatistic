using System.Globalization;
using MiniExcelLibs;
using Spectre.Console;

namespace ObjectAnalyzer.Reporting;

/// <summary>
/// 将分析结果导出为 Excel 工作簿（.xlsx，多 sheet），便于在 Excel/WPS/LibreOffice 中
/// 直接浏览、筛选、做透视与图表。覆盖字段统计、重复对象、违规清单及 深度分析七项。
/// </summary>
public static class ExcelReportExporter
{
    public static void Save(AnalysisResult result, string outputPath)
    {
        AnsiConsole.MarkupLine($"正在将 Excel 报告保存至: [blue]{Markup.Escape(outputPath)}[/] ...");

        // MiniExcel 的 sheet 顺序按 Dictionary 插入顺序输出
        var sheets = new Dictionary<string, object>();

        AppendSummarySheet(sheets, result);
        AppendFieldStatsSheet(sheets, result);
        AppendDuplicateSheet(sheets, result);
        AppendViolationsSheet(sheets, result);

        if (result.StringPatterns is { Count: > 0 })
            AppendStringPatternsSheet(sheets, result);
        if (result.UnicodeHygiene is { Count: > 0 })
            AppendUnicodeHygieneSheet(sheets, result);
        if (result.FuzzyDuplicates is { Groups.Count: > 0 })
            AppendFuzzyDedupSheet(sheets, result);
        if (result.CorrelationMatrix is { Fields.Count: > 0 })
            AppendCorrelationSheet(sheets, result);
        if (result.TimeSeriesAnalyses is { Count: > 0 })
            AppendTimeSeriesSheet(sheets, result);
        if (result.DistributionDiff is not null)
            AppendDistributionDiffSheet(sheets, result);
        if (result.SamplingConfidence is not null)
            AppendSamplingConfidenceSheet(sheets, result);

        // 无需特别配置：MiniExcel 默认就会按数据内容自适应列宽
        MiniExcel.SaveAs(outputPath, sheets, overwriteFile: true);
        AnsiConsole.MarkupLine($"[green]Excel 报告成功保存！[/]");
    }

    // ───────────── 摘要 ─────────────

    private static void AppendSummarySheet(Dictionary<string, object> sheets, AnalysisResult r)
    {
        var rows = new List<Dictionary<string, object>>
        {
            new() { ["指标"] = "被分析对象总数", ["值"] = r.TotalObjects },
            new() { ["指标"] = "去重后对象数", ["值"] = r.TotalUniqueObjects },
            new() { ["指标"] = "重复对象组数", ["值"] = r.TotalDuplicateGroups },
            new() { ["指标"] = "字段总数", ["值"] = r.FieldStatistics.Count },
            new() { ["指标"] = "质量违规字段数", ["值"] = r.QualityViolations.Select(v => v.Field).Distinct().Count() },
            new() { ["指标"] = "质量违规总数", ["值"] = r.QualityViolations.Count },
            new() { ["指标"] = "深度分析字符串模式字段数", ["值"] = r.StringPatterns?.Count ?? 0 },
            new() { ["指标"] = "深度分析 Unicode 卫生字段数", ["值"] = r.UnicodeHygiene?.Count ?? 0 },
            new() { ["指标"] = "深度分析模糊去重组数", ["值"] = r.FuzzyDuplicates?.Groups.Count ?? 0 },
            new() { ["指标"] = "深度分析相关性矩阵字段数", ["值"] = r.CorrelationMatrix?.Fields.Count ?? 0 },
            new() { ["指标"] = "深度分析时间序列字段数", ["值"] = r.TimeSeriesAnalyses?.Count ?? 0 },
            new() { ["指标"] = "深度分析分布漂移显著字段数", ["值"] = r.DistributionDiff?.SignificantDriftCount ?? 0 },
        };
        if (!string.IsNullOrEmpty(r.FilterExpression))
        {
            rows.Add(new() { ["指标"] = "过滤表达式", ["值"] = r.FilterExpression! });
        }
        sheets["摘要"] = rows;
    }

    // ───────────── 字段统计 ─────────────

    private static void AppendFieldStatsSheet(Dictionary<string, object> sheets, AnalysisResult r)
    {
        var rows = r.FieldStatistics.Select(s => new Dictionary<string, object>
        {
            ["字段名"] = s.FieldName,
            ["样本数"] = s.Count,
            ["覆盖率"] = s.Coverage,
            ["主导类型"] = s.PrimaryType,
            ["语义类型"] = s.SemanticType ?? "",
            ["Null 出现"] = s.NullAppeared ? "是" : "否",
            ["唯一值数"] = (s.DistinctCount ?? s.ValueCounts.Count),
            ["唯一值近似"] = s.DistinctApproximate ? "是" : "否",
            ["最小值"] = s.MinNumber?.ToString("G4", CultureInfo.InvariantCulture) ?? "",
            ["最大值"] = s.MaxNumber?.ToString("G4", CultureInfo.InvariantCulture) ?? "",
            ["均值"] = s.AvgNumber?.ToString("G4", CultureInfo.InvariantCulture) ?? "",
            ["总和"] = s.SumNumber?.ToString("G4", CultureInfo.InvariantCulture) ?? "",
            ["标准差"] = s.StdDevNumber?.ToString("G4", CultureInfo.InvariantCulture) ?? "",
            ["中位数 P50"] = s.MedianNumber?.ToString("G4", CultureInfo.InvariantCulture) ?? "",
            ["P90"] = s.P90Number?.ToString("G4", CultureInfo.InvariantCulture) ?? "",
            ["P95"] = s.P95Number?.ToString("G4", CultureInfo.InvariantCulture) ?? "",
            ["P99"] = s.P99Number?.ToString("G4", CultureInfo.InvariantCulture) ?? "",
            ["最小长度"] = s.MinLength?.ToString(CultureInfo.InvariantCulture) ?? "",
            ["最大长度"] = s.MaxLength?.ToString(CultureInfo.InvariantCulture) ?? "",
            ["平均长度"] = s.AvgLength?.ToString("G4", CultureInfo.InvariantCulture) ?? "",
            ["转换类型"] = s.TransformType ?? "",
            ["转换失败数"] = s.TransformFailureCount,
            ["枚举违规数"] = s.EnumViolations.Count,
            ["类型分布"] = string.Join("; ", s.TypeCounts.Select(t => $"{t.Key}={t.Value}")),
            ["Top5 值"] = string.Join("; ", s.ValueCounts.OrderByDescending(v => v.Value).Take(5).Select(v => $"{v.Key}×{v.Value}")),
        }).ToList();
        sheets["字段统计"] = rows;
    }

    private static void AppendDuplicateSheet(Dictionary<string, object> sheets, AnalysisResult r)
    {
        sheets["重复对象"] = r.DuplicateObjects.Select(d => new Dictionary<string, object>
        {
            ["重复次数"] = d.Count,
            ["对象内容"] = d.ObjectJson,
        }).ToList();
    }

    private static void AppendViolationsSheet(Dictionary<string, object> sheets, AnalysisResult r)
    {
        sheets["违规清单"] = r.QualityViolations.Select(v => new Dictionary<string, object>
        {
            ["字段"] = v.Field,
            ["规则类型"] = v.Check,
            ["违规数"] = v.Count,
            ["是否近似"] = v.Approximate ? "是" : "否",
            ["样例"] = string.Join(" | ", v.Samples.Take(5)),
            ["说明"] = v.Message,
        }).ToList();
    }

    // ───────────── 深度分析：字符串模式 ─────────────

    private static void AppendStringPatternsSheet(Dictionary<string, object> sheets, AnalysisResult r)
    {
        var rows = new List<Dictionary<string, object>>();
        foreach (var (field, profile) in r.StringPatterns!.OrderByDescending(p => p.Value.TotalSamples))
        {
            var top = string.Join(" | ", profile.Patterns.Take(5).Select(t =>
                $"{t.Template}×{t.Count} ({(t.Ratio?.ToString("P0", CultureInfo.InvariantCulture) ?? "—")})"));
            rows.Add(new Dictionary<string, object>
            {
                ["字段"] = field,
                ["样本数"] = profile.TotalSamples,
                ["主导模板"] = profile.DominantPattern?.Template ?? "",
                ["主导模板命中数"] = profile.DominantPattern?.Count ?? 0,
                ["Top 5 模板"] = top,
            });
        }
        sheets["字符串模式"] = rows;
    }

    // ───────────── 深度分析：Unicode 卫生 ─────────────

    private static void AppendUnicodeHygieneSheet(Dictionary<string, object> sheets, AnalysisResult r)
    {
        var rows = r.UnicodeHygiene!.Select(kv => new Dictionary<string, object>
        {
            ["字段"] = kv.Key,
            ["卫生评分"] = kv.Value.HygieneScore?.ToString("F1", CultureInfo.InvariantCulture) ?? "",
            ["样本数"] = kv.Value.TotalSamples,
            ["全角字符数"] = kv.Value.FullwidthCount,
            ["半角字符数"] = kv.Value.HalfwidthCount,
            ["全角占比"] = kv.Value.FullwidthRatio?.ToString("P1", CultureInfo.InvariantCulture) ?? "",
            ["不可见字符数"] = kv.Value.InvisibleCount,
            ["控制字符数"] = kv.Value.ControlCharCount,
            ["零宽字符数"] = kv.Value.ZeroWidthCount,
            ["双向控制符"] = kv.Value.BidiControlCount,
            ["前后空白"] = kv.Value.SurroundingWhitespaceCount,
            ["大小写混用"] = kv.Value.MixedCaseCount,
            ["大小写混用率"] = kv.Value.MixedCaseRatio?.ToString("P1", CultureInfo.InvariantCulture) ?? "",
        }).ToList();
        sheets["Unicode 卫生"] = rows;
    }

    // ───────────── 深度分析：模糊去重 ─────────────

    private static void AppendFuzzyDedupSheet(Dictionary<string, object> sheets, AnalysisResult r)
    {
        var rows = r.FuzzyDuplicates!.Groups.Select(g => new Dictionary<string, object>
        {
            ["规范化键"] = g.CanonicalKey,
            ["组大小"] = g.Count,
            ["平均相似度"] = g.AverageSimilarity.ToString("F3", CultureInfo.InvariantCulture),
            ["代表对象"] = g.RepresentativeJson,
            ["样例"] = string.Join(" | ", g.Examples.Take(5)),
        }).ToList();
        sheets["模糊去重"] = rows;
    }

    // ───────────── 深度分析：字段相关性 ─────────────

    private static void AppendCorrelationSheet(Dictionary<string, object> sheets, AnalysisResult r)
    {
        var cm = r.CorrelationMatrix!;
        var summary = new List<Dictionary<string, object>>
        {
            new() { ["指标"] = "样本数", ["值"] = cm.Samples },
            new() { ["指标"] = "参与字段数", ["值"] = cm.Fields.Count },
        };
        if (cm.StrongPairs.Count > 0)
        {
            summary.Add(new() { ["指标"] = "强相关对数", ["值"] = cm.StrongPairs.Count });
        }
        sheets["相关性摘要"] = summary;

        if (cm.StrongPairs.Count > 0)
        {
            sheets["强相关对"] = cm.StrongPairs.Select(p => new Dictionary<string, object>
            {
                ["字段 A"] = p.FieldA,
                ["字段 B"] = p.FieldB,
                ["Pearson"] = p.Pearson?.ToString("F4", CultureInfo.InvariantCulture) ?? "",
                ["Spearman"] = p.Spearman?.ToString("F4", CultureInfo.InvariantCulture) ?? "",
                ["样本数"] = p.Samples,
            }).ToList();
        }

        // 完整矩阵（用二维 list 表示，MiniExcel 原生支持）
        sheets["Pearson 矩阵"] = BuildMatrix(cm.Fields, cm.Pearson);
        sheets["Spearman 矩阵"] = BuildMatrix(cm.Fields, cm.Spearman);
    }

    private static List<Dictionary<string, object>> BuildMatrix(List<string> fields, List<List<double?>> matrix)
    {
        var header = new Dictionary<string, object> { [""] = "" };
        foreach (var f in fields) header[f] = f;
        var rows = new List<Dictionary<string, object>> { header };
        for (int i = 0; i < fields.Count; i++)
        {
            var row = new Dictionary<string, object> { [""] = fields[i] };
            for (int j = 0; j < fields.Count; j++)
            {
                var v = (i < matrix.Count && j < matrix[i].Count) ? matrix[i][j] : null;
                row[fields[j]] = v?.ToString("F4", CultureInfo.InvariantCulture) ?? "—";
            }
            rows.Add(row);
        }
        return rows;
    }

    // ───────────── 深度分析：时间序列深度 ─────────────

    private static void AppendTimeSeriesSheet(Dictionary<string, object> sheets, AnalysisResult r)
    {
        sheets["时间序列深度"] = r.TimeSeriesAnalyses!.Select(kv => new Dictionary<string, object>
        {
            ["字段"] = kv.Key,
            ["趋势"] = kv.Value.Trend,
            ["斜率"] = kv.Value.TrendSlope?.ToString("F4", CultureInfo.InvariantCulture) ?? "",
            ["Theil-Sen 斜率"] = kv.Value.TheilSenSlope?.ToString("F4", CultureInfo.InvariantCulture) ?? "",
            ["R²"] = kv.Value.TrendRSquared?.ToString("F3", CultureInfo.InvariantCulture) ?? "",
            ["周期(桶)"] = kv.Value.DetectedPeriod?.ToString(CultureInfo.InvariantCulture) ?? "",
            ["周期强度"] = kv.Value.PeriodicityStrength?.ToString("F3", CultureInfo.InvariantCulture) ?? "",
            ["最大间隔(桶)"] = kv.Value.LongestGapBuckets?.ToString(CultureInfo.InvariantCulture) ?? "",
            ["空桶占比"] = kv.Value.EmptyRatio.ToString("P1", CultureInfo.InvariantCulture),
            ["脉冲性"] = kv.Value.Burstiness.ToString("F2", CultureInfo.InvariantCulture),
            ["桶数"] = kv.Value.BucketCount,
            ["样本数"] = kv.Value.TotalSamples,
        }).ToList();
    }

    // ───────────── 深度分析：分布漂移 ─────────────

    private static void AppendDistributionDiffSheet(Dictionary<string, object> sheets, AnalysisResult r)
    {
        var d = r.DistributionDiff!;
        var summary = new List<Dictionary<string, object>>
        {
            new() { ["指标"] = "显著漂移字段数", ["值"] = d.SignificantDriftCount },
            new() { ["指标"] = "新增字段数", ["值"] = d.AddedFields.Count },
            new() { ["指标"] = "删除字段数", ["值"] = d.RemovedFields.Count },
        };
        if (d.AddedFields.Count > 0)
            summary.Add(new() { ["指标"] = "新增字段清单", ["值"] = string.Join(", ", d.AddedFields.Select(f => f.Path)) });
        if (d.RemovedFields.Count > 0)
            summary.Add(new() { ["指标"] = "删除字段清单", ["值"] = string.Join(", ", d.RemovedFields.Select(f => f.Path)) });
        sheets["分布漂移摘要"] = summary;

        if (d.CoverageChanges.Count > 0)
        {
            sheets["覆盖率变化"] = d.CoverageChanges.Select(c => new Dictionary<string, object>
            {
                ["字段"] = c.Path,
                ["基线覆盖率"] = c.BaselineCoverage.ToString("P1", CultureInfo.InvariantCulture),
                ["当前覆盖率"] = c.CurrentCoverage.ToString("P1", CultureInfo.InvariantCulture),
                ["Δ"] = c.Delta.ToString("F3", CultureInfo.InvariantCulture),
            }).ToList();
        }
        if (d.NumericDrifts.Count > 0)
        {
            sheets["数值漂移"] = d.NumericDrifts.Select(n => new Dictionary<string, object>
            {
                ["字段"] = n.Path,
                ["基线均值"] = n.BaselineMean?.ToString("F4", CultureInfo.InvariantCulture) ?? "",
                ["当前均值"] = n.CurrentMean?.ToString("F4", CultureInfo.InvariantCulture) ?? "",
                ["均值偏移"] = n.MeanShift?.ToString("F4", CultureInfo.InvariantCulture) ?? "",
                ["偏移 σ 倍数"] = n.MeanShiftSigma?.ToString("F2", CultureInfo.InvariantCulture) ?? "",
                ["基线 P50"] = n.BaselineMedian?.ToString("F4", CultureInfo.InvariantCulture) ?? "",
                ["当前 P50"] = n.CurrentMedian?.ToString("F4", CultureInfo.InvariantCulture) ?? "",
                ["基线 P95"] = n.BaselineP95?.ToString("F4", CultureInfo.InvariantCulture) ?? "",
                ["当前 P95"] = n.CurrentP95?.ToString("F4", CultureInfo.InvariantCulture) ?? "",
            }).ToList();
        }
        if (d.DistinctDrifts.Count > 0)
        {
            sheets["Distinct 漂移"] = d.DistinctDrifts.Select(c => new Dictionary<string, object>
            {
                ["字段"] = c.Path,
                ["基线 distinct"] = c.BaselineDistinct?.ToString(CultureInfo.InvariantCulture) ?? "",
                ["当前 distinct"] = c.CurrentDistinct?.ToString(CultureInfo.InvariantCulture) ?? "",
                ["Δ 比例"] = c.DeltaRatio?.ToString("P1", CultureInfo.InvariantCulture) ?? "",
                ["Jaccard"] = c.Jaccard?.ToString("F4", CultureInfo.InvariantCulture) ?? "",
                ["严重度"] = c.Severity,
            }).ToList();
        }
    }

    // ───────────── 深度分析：采样置信 ─────────────

    private static void AppendSamplingConfidenceSheet(Dictionary<string, object> sheets, AnalysisResult r)
    {
        var sc = r.SamplingConfidence!;
        var summary = new List<Dictionary<string, object>>
        {
            new() { ["指标"] = "采样方法", ["值"] = sc.SamplingMethod ?? "" },
            new() { ["指标"] = "有效样本数", ["值"] = sc.EffectiveSampleSize },
            new() { ["指标"] = "原始记录数(估计)", ["值"] = sc.EstimatedTotal },
            new() { ["指标"] = "采样比例", ["值"] = sc.SamplingFraction.ToString("P2", CultureInfo.InvariantCulture) },
            new() { ["指标"] = "置信水平", ["值"] = sc.ConfidenceLevel.ToString("P0", CultureInfo.InvariantCulture) },
            new() { ["指标"] = "低可信字段数", ["值"] = sc.LowConfidenceFields.Count },
        };
        if (!string.IsNullOrEmpty(sc.Summary))
            summary.Add(new() { ["指标"] = "采样说明", ["值"] = sc.Summary });
        sheets["采样置信摘要"] = summary;

        if (sc.CoverageIntervals is { Count: > 0 })
        {
            sheets["字段覆盖率区间"] = sc.CoverageIntervals.Select(kv => new Dictionary<string, object>
            {
                ["字段"] = kv.Key,
                ["观察覆盖率"] = kv.Value.Observed.ToString("P1", CultureInfo.InvariantCulture),
                ["下限"] = kv.Value.Lower.ToString("P1", CultureInfo.InvariantCulture),
                ["上限"] = kv.Value.Upper.ToString("P1", CultureInfo.InvariantCulture),
                ["区间宽度"] = kv.Value.Width.ToString("F3", CultureInfo.InvariantCulture),
                ["歧义(下界=0)"] = kv.Value.Ambiguous ? "是" : "否",
            }).ToList();
        }
    }
}
