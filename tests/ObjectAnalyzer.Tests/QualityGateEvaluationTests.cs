using ObjectAnalyzer.Analysis.Gates;

namespace ObjectAnalyzer.Tests;

/// <summary>
/// 结构化质量门禁结果测试：确认新 API 层不会改变既有门禁判断语义。
/// </summary>
public sealed class QualityGateEvaluationTests
{
    [Fact]
    public void EvaluateAll_AllPass_ReturnsEveryRegisteredGateAsPassed()
    {
        var results = QualityGateEvaluation.EvaluateAll(
            new AnalysisResult(),
            new AnalysisOptions());

        Assert.Equal(QualityGateRegistry.Gates.Count, results.Count);
        Assert.All(results, result => Assert.False(result.Triggered));
        Assert.Equal([3, 4, 5, 6], results.Select(result => result.ExitCode).ToArray());
    }

    [Fact]
    public void EvaluateAll_QualityViolation_ReportsStructuredFailure()
    {
        var analysisResult = new AnalysisResult
        {
            QualityViolations = new List<FieldViolation>
            {
                new() { Field = "age", Check = "enum", Count = 1, Message = "存在非法值" }
            }
        };
        var options = new AnalysisOptions
        {
            Gates = { FailOnViolations = true }
        };

        var results = QualityGateEvaluation.EvaluateAll(analysisResult, options);
        var quality = Assert.Single(results, result => result.ExitCode == 3);

        Assert.True(quality.Triggered);
        Assert.Equal("数据质量", quality.Name);
        Assert.Equal("数据质量门禁未通过", quality.FailureMessage);
    }

    [Fact]
    public void GetFirstFailure_UsesExistingRegistryOrder()
    {
        var analysisResult = new AnalysisResult
        {
            QualityViolations = new List<FieldViolation>
            {
                new() { Check = "enum", Count = 1 }
            }
        };
        var options = new AnalysisOptions
        {
            Gates = { FailOnViolations = true }
        };

        var failure = QualityGateEvaluation.GetFirstFailure(analysisResult, options);

        Assert.True(failure.HasValue);
        Assert.Equal(3, failure.Value.ExitCode);
        Assert.Equal("数据质量", failure.Value.Name);
    }

    [Fact]
    public void GetFirstFailure_AllPass_ReturnsNull()
    {
        var failure = QualityGateEvaluation.GetFirstFailure(
            new AnalysisResult(),
            new AnalysisOptions());

        Assert.Null(failure);
    }
}
