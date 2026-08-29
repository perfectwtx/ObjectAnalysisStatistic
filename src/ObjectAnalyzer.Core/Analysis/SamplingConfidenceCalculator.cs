namespace ObjectAnalyzer.Analysis;

/// <summary>
/// 采样置信区间计算器：基于 Wilson score interval（Wald 的小样本稳健替代）。
/// </summary>
public static class SamplingConfidenceCalculator
{
    /// <summary>区间宽度 ≥ 此值视为"低可信"。</summary>
    public const double LowConfidenceWidth = 0.2;

    /// <summary>95% 置信水平对应的 z 值（双侧）。</summary>
    private const double Z95 = 1.96;

    /// <summary>为单字段的"出现 / 样本"对计算 Wilson 95% 置信区间。</summary>
    public static CoverageInterval Compute(double observed, long sampleSize, double confidenceLevel = 0.95)
    {
        var ci = new CoverageInterval { Observed = observed };
        if (sampleSize <= 0)
        {
            ci.Ambiguous = true;
            return ci;
        }
        double z = confidenceLevel >= 0.99 ? 2.576 : Z95;
        double p = Math.Clamp(observed, 0.0, 1.0);
        double n = sampleSize;
        double z2 = z * z;
        double denom = 1.0 + z2 / n;
        double center = (p + z2 / (2 * n)) / denom;
        double margin = z * Math.Sqrt((p * (1 - p) + z2 / (4 * n)) / n) / denom;
        ci.Lower = Math.Max(0.0, center - margin);
        ci.Upper = Math.Min(1.0, center + margin);
        ci.Width = ci.Upper - ci.Lower;
        ci.Ambiguous = ci.Upper < 0.05;
        return ci;
    }

    /// <summary>从 (FieldName, Count, Total) 元组列表构建 SamplingConfidence。</summary>
    public static SamplingConfidence Build(
        string method,
        long sampleSize,
        long estimatedTotal,
        IEnumerable<(string Field, long Count)> fieldCounts,
        long total)
    {
        var sc = new SamplingConfidence
        {
            SamplingMethod = method,
            EffectiveSampleSize = sampleSize,
            EstimatedTotal = estimatedTotal,
            SamplingFraction = estimatedTotal > 0 ? (double)sampleSize / estimatedTotal : 1.0
        };
        if (total > 0)
        {
            foreach (var (field, count) in fieldCounts)
            {
                double obs = (double)count / total;
                var ci = Compute(obs, total);
                sc.CoverageIntervals[field] = ci;
                if (ci.Width >= LowConfidenceWidth || ci.Ambiguous)
                {
                    sc.LowConfidenceFields.Add(field);
                }
            }
        }
        sc.Summary = $"采样方式: {method}; 有效样本: {sampleSize}; 原始规模(估): {estimatedTotal}; 比例: {sc.SamplingFraction:P2}。" +
            (sc.LowConfidenceFields.Count > 0
                ? $" 提示: {sc.LowConfidenceFields.Count} 个字段覆盖率置信区间过宽，统计可信度低。"
                : " 所有字段覆盖率置信区间均较窄（宽度 < 0.2），统计可信。");
        return sc;
    }
}
