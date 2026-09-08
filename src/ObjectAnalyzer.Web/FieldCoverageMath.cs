using ObjectAnalyzer.Analysis;

namespace ObjectAnalyzer.Web;

/// <summary>
/// 字段覆盖率三段拆分（W-覆盖率）：把"未覆盖"区分为两种原因——
/// 值为 null（<see cref="FieldStatistic.NullCount"/>，Count 的子集）与 字段不存在（键缺失 = 总数 − Count）。
/// 供字段表格的堆叠条与明细行使用；百分比均以总对象数为分母。
/// </summary>
public static class FieldCoverageMath
{
    public sealed record Breakdown(
        long NonNullCount,
        long MissingCount,
        double ActualPercent,
        double NullPercent,
        double MissingPercent,
        bool HasUncovered);

    public static Breakdown Compute(FieldStatistic field, long totalObjects)
    {
        long nonNull = Math.Max(0, field.Count - field.NullCount);
        long missing = Math.Max(0, totalObjects - field.Count);
        double total = totalObjects;

        return new Breakdown(
            NonNullCount: nonNull,
            MissingCount: missing,
            ActualPercent: total > 0 ? nonNull * 100.0 / total : 0,
            NullPercent: total > 0 ? field.NullCount * 100.0 / total : 0,
            MissingPercent: total > 0 ? missing * 100.0 / total : 0,
            HasUncovered: field.NullCount > 0 || missing > 0);
    }
}
