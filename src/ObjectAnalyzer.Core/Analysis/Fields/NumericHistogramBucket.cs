namespace ObjectAnalyzer.Analysis;

using System.Globalization;

/// <summary>数值直方图分桶（对数定宽，桶边界为 2 的幂；零值单独成桶 From=To=0）。</summary>
public sealed class NumericHistogramBucket
{
    /// <summary>桶下界（含）。正数桶与零桶为闭区间下界；负数桶表示开区间下界。</summary>
    public double From { get; init; }

    /// <summary>桶上界。正数桶为开区间上界，负数桶为闭区间上界。</summary>
    public double To { get; init; }

    /// <summary>落入该桶的数值出现次数。</summary>
    public long Count { get; set; }

    /// <summary>紧凑区间标签："0"、"[4,8)"（正数桶，左闭右开）、"(-8,-4]"（负数桶，左开右闭）。</summary>
    public override string ToString()
    {
        if (From == 0 && To == 0)
            return "0";
        return From > 0
            ? string.Create(CultureInfo.InvariantCulture, $"[{From:G3},{To:G3})")
            : string.Create(CultureInfo.InvariantCulture, $"({From:G3},{To:G3}]");
    }
}
