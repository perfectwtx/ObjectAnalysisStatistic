using ObjectAnalyzer.Analysis.Gates;

namespace ObjectAnalyzer.Tests;

public sealed class QualityGateEvaluationTests
{
    [Fact]
    public void EvaluateAll_AllPass_ReturnsEveryRegisteredGateAsPassed()
    {
        var results = QualityGateEvaluation.EvaluateAll(new AnalysisResult(), new AnalysisOptions());

        Assert.Equal(QualityGateRegistry.Gates.Count, results.Count);
        Assert.All(results, result => Assert.False(result.Triggered));
        Assert.Equal([3, 4, 5, 6, 7], results.Select(result => result.ExitCode).ToArray());
    }

    [Fact]
    public void EvaluateAll_QualityViolation_ReportsStructuredFailure()
    {
        var result = new AnalysisResult
        {
            QualityViolations = [new() { Field = "age", Check = "enum", Count = 1, Message = "存在非法值" }]
        };
        var options = new AnalysisOptions { Gates = { FailOnViolations = true } };

        var quality = Assert.Single(QualityGateEvaluation.EvaluateAll(result, options), x => x.ExitCode == 3);

        Assert.True(quality.Triggered);
        Assert.Equal("数据质量", quality.Name);
        Assert.Equal("数据质量门禁未通过", quality.FailureMessage);
    }

    [Fact]
    public void EvaluateAll_OverallScoreBelow_TriggersScoreGate()
    {
        var result = new AnalysisResult
        {
            TotalObjects = 100,
            TotalUniqueObjects = 50,
            FieldStatistics = [new() { FieldName = "name", Coverage = 0.5 }]
        };
        var options = new AnalysisOptions { Gates = { FailOnOverallScoreBelow = 90 } };

        var scoreGate = Assert.Single(QualityGateEvaluation.EvaluateAll(result, options), x => x.ExitCode == 7);

        Assert.True(scoreGate.Triggered);
        Assert.Equal("质量评分", scoreGate.Name);
        Assert.Equal("质量评分门禁未通过", scoreGate.FailureMessage);
    }

    [Fact]
    public void EvaluateAll_ScoreThresholds_AllPass_DoesNotTriggerScoreGate()
    {
        var result = new AnalysisResult
        {
            TotalObjects = 100,
            TotalUniqueObjects = 100,
            FieldStatistics = [new() { FieldName = "name", Coverage = 1.0 }]
        };
        var options = new AnalysisOptions
        {
            Gates =
            {
                FailOnOverallScoreBelow = 90,
                FailOnCompletenessBelow = 95,
                FailOnValidityBelow = 95,
                FailOnUniquenessBelow = 95,
                FailOnConsistencyBelow = 95,
                FailOnAnomalyControlBelow = 95
            }
        };

        var scoreGate = Assert.Single(QualityGateEvaluation.EvaluateAll(result, options), x => x.ExitCode == 7);
        Assert.False(scoreGate.Triggered);
    }

    [Fact]
    public void GetFirstFailure_UsesExistingRegistryOrder()
    {
        var result = new AnalysisResult
        {
            QualityViolations = [new() { Check = "enum", Count = 1 }]
        };
        var options = new AnalysisOptions { Gates = { FailOnViolations = true } };

        var failure = QualityGateEvaluation.GetFirstFailure(result, options);

        Assert.True(failure.HasValue);
        Assert.Equal(3, failure.Value.ExitCode);
        Assert.Equal("数据质量", failure.Value.Name);
    }

    [Fact]
    public void GetFirstFailure_AllPass_ReturnsNull()
    {
        Assert.Null(QualityGateEvaluation.GetFirstFailure(new AnalysisResult(), new AnalysisOptions()));
    }
}
