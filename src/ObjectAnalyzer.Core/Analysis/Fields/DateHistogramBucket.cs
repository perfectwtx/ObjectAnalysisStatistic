namespace ObjectAnalyzer.Analysis;
/// <summary>时间戳直方图分桶（按跨度自动切档：秒/分/时/天/周/月/年；桶为闭区间下界、开区间上界）。</summary>
public sealed class DateHistogramBucket
{
    /// <summary>桶下界（含），闭区间。</summary>
    public DateTimeOffset From { get; init; }

    /// <summary>桶上界（不含），开区间。</summary>
    public DateTimeOffset To { get; init; }

    /// <summary>落入该桶的时间戳出现次数。</summary>
    public long Count { get; set; }

    /// <summary>紧凑区间标签，按桶跨度自适应：秒级/分级/时级/天级（YYYY-MM-DD）/周级/月份（YYYY-MM）/年份（YYYY）。</summary>
    public override string ToString()
    {
        TimeSpan span = To - From;
        if (span.TotalSeconds <= 120)
        {
            return $"[{From:HH:mm:ss}~{To:HH:mm:ss})";
        }
        if (span.TotalMinutes <= 120)
        {
            return $"[{From:HH:mm}~{To:HH:mm})";
        }
        if (span.TotalHours <= 48)
        {
            return $"[{From:MM-dd HH:mm}~{To:MM-dd HH:mm})";
        }
        if (span.TotalDays <= 14)
        {
            return $"[{From:yyyy-MM-dd}~{To:yyyy-MM-dd})";
        }
        if (span.TotalDays <= 90)
        {
            return $"[{From:yyyy-MM-dd} W{ISOWeek(From)},{To:yyyy-MM-dd} W{ISOWeek(To)})";
        }
        if (span.TotalDays <= 730)
        {
            return $"[{From:yyyy-MM}~{To:yyyy-MM})";
        }
        return $"[{From:yyyy}~{To:yyyy})";
    }

    private static int ISOWeek(DateTimeOffset d)
    {
        var day = (System.DayOfWeek)((((int)d.DayOfWeek + 6) % 7) + 1); // Monday=1..Sunday=7
        return System.Globalization.ISOWeek.GetWeekOfYear(d.DateTime);
    }
}
