using System.Text.Json;
using System.Text.Json.Nodes;
using ObjectAnalyzer.Analysis;

namespace ObjectAnalyzer.Tests;

public class AnalysisOptionsApplyRuntimeTests
{
    private static AnalysisOptions NewDefaults() => new() { Input = { InputPath = "data.json" } };
    private static void Apply(AnalysisOptions options, string runtimeJson) => RulesToOptionsConverter.Apply(options, new AnalysisRules { Runtime = JsonNode.Parse(runtimeJson) as JsonObject });

    [Fact]
    public void ApplyRuntime_GatesScoreThresholds()
    {
        var options = NewDefaults();
        Apply(options, """{ "gates": { "failOnOverallScoreBelow": 90, "failOnCompletenessBelow": 95, "failOnValidityBelow": 98, "failOnUniquenessBelow": 95, "failOnConsistencyBelow": 95, "failOnAnomalyControlBelow": 95 } }""");

        Assert.Equal(90, options.Gates.FailOnOverallScoreBelow);
        Assert.Equal(95, options.Gates.FailOnCompletenessBelow);
        Assert.Equal(98, options.Gates.FailOnValidityBelow);
        Assert.Equal(95, options.Gates.FailOnUniquenessBelow);
        Assert.Equal(95, options.Gates.FailOnConsistencyBelow);
        Assert.Equal(95, options.Gates.FailOnAnomalyControlBelow);
        Assert.True(options.Gates.HasScoreThresholds);
    }

    [Fact]
    public void ApplyRuntime_ScoreThresholdsDoNotOverrideExplicitValues()
    {
        var options = NewDefaults();
        options.Gates.FailOnOverallScoreBelow = 80;

        Apply(options, """{ "gates": { "failOnOverallScoreBelow": 90 } }""");

        Assert.Equal(80, options.Gates.FailOnOverallScoreBelow);
    }

    [Fact]
    public void ApplyRuntime_GatesFailOnViolationsAndOutlierRatio()
    {
        var options = NewDefaults();
        Apply(options, """{ "gates": { "failOnViolations": true, "failOnOutlierRatio": 0.05 } }""");
        Assert.True(options.Gates.FailOnViolations);
        Assert.Equal(0.05, options.Gates.FailOnOutlierRatio);
    }
}
