using ObjectAnalyzer.Analysis;
using Xunit;

namespace ObjectAnalyzer.Web.Tests;

/// <summary>
/// 字段覆盖率三段拆分的数学：实际（非空）/ null / 字段缺失，均以总对象数为分母。
/// 该计算直接驱动字段表格的堆叠条与明细行显示。
/// </summary>
public class FieldCoverageMathTests
{
    [Fact]
    public void Compute_MixedNullAndAbsent_SplitsAllThreeSegments()
    {
        // 10 条记录：非空 6、null 2、键缺失 2
        var field = new FieldStatistic { FieldName = "age", Count = 8, NullCount = 2 };

        var cov = FieldCoverageMath.Compute(field, totalObjects: 10);

        Assert.Equal(6, cov.NonNullCount);
        Assert.Equal(2, cov.MissingCount);
        Assert.Equal(60.0, cov.ActualPercent, precision: 5);
        Assert.Equal(20.0, cov.NullPercent, precision: 5);
        Assert.Equal(20.0, cov.MissingPercent, precision: 5);
        Assert.True(cov.HasUncovered);
    }

    [Fact]
    public void Compute_FullyCovered_NoUncoveredFlag()
    {
        var field = new FieldStatistic { FieldName = "name", Count = 5, NullCount = 0 };

        var cov = FieldCoverageMath.Compute(field, totalObjects: 5);

        Assert.Equal(5, cov.NonNullCount);
        Assert.Equal(0, cov.MissingCount);
        Assert.Equal(100.0, cov.ActualPercent, precision: 5);
        Assert.Equal(0.0, cov.NullPercent, precision: 5);
        Assert.False(cov.HasUncovered);
    }

    [Fact]
    public void Compute_AllNull_ZeroActualCoverage()
    {
        var field = new FieldStatistic { FieldName = "x", Count = 4, NullCount = 4 };

        var cov = FieldCoverageMath.Compute(field, totalObjects: 4);

        Assert.Equal(0, cov.NonNullCount);
        Assert.Equal(0.0, cov.ActualPercent, precision: 5);
        Assert.Equal(100.0, cov.NullPercent, precision: 5);
        Assert.True(cov.HasUncovered);
    }

    [Fact]
    public void Compute_EmptyResult_ZeroDivisionSafe()
    {
        var field = new FieldStatistic { FieldName = "x", Count = 0, NullCount = 0 };

        var cov = FieldCoverageMath.Compute(field, totalObjects: 0);

        Assert.Equal(0.0, cov.ActualPercent, precision: 5);
        Assert.Equal(0.0, cov.NullPercent, precision: 5);
        Assert.Equal(0.0, cov.MissingPercent, precision: 5);
        Assert.False(cov.HasUncovered);
    }

    [Fact]
    public void Compute_AllAbsent_CountZero()
    {
        // 字段从未出现（如某些行才有的可选键在无该键的数据里）
        var field = new FieldStatistic { FieldName = "ghost", Count = 0, NullCount = 0 };

        var cov = FieldCoverageMath.Compute(field, totalObjects: 4);

        Assert.Equal(0, cov.NonNullCount);
        Assert.Equal(4, cov.MissingCount);
        Assert.Equal(100.0, cov.MissingPercent, precision: 5);
        Assert.True(cov.HasUncovered);
    }
}
