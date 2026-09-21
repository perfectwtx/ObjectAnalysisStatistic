using ObjectAnalyzer.Analysis;
using ObjectAnalyzer.Desktop.ViewModels;

namespace ObjectAnalyzer.Desktop.Tests;

public sealed class MainViewModelTests
{
    [Fact]
    public void SetCompleted_updates_result_summary_and_state()
    {
        var viewModel = new MainViewModel();
        var result = CreateResult();

        viewModel.SetCompleted(result);

        Assert.Same(result, viewModel.Result);
        Assert.True(viewModel.HasResult);
        Assert.Equal(result.TotalObjects, viewModel.TotalObjects);
        Assert.Equal(result.FieldStatistics.Count, viewModel.FieldCount);
        Assert.Equal(result.TotalDuplicateGroups, viewModel.DuplicateGroups);
        Assert.Equal(result.QualityViolations.Count, viewModel.QualityIssueCount);
        Assert.Equal(100, viewModel.Progress);
        Assert.Equal("分析完成", viewModel.Status);
        Assert.Single(viewModel.Fields);
    }

    [Fact]
    public void TryBuildBaseline_rejects_threshold_out_of_range()
    {
        var viewModel = new MainViewModel
        {
            BaselineSourceName = "sample",
            MinimumOverallScore = "101"
        };

        var success = viewModel.TryBuildBaseline(out var baseline, out var error);

        Assert.False(success);
        Assert.Null(baseline);
        Assert.Contains("0 到 100", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TryBuildBaseline_accepts_valid_thresholds()
    {
        var viewModel = new MainViewModel
        {
            BaselineSourceName = "sample",
            MinimumOverallScore = "91.5",
            MinimumCompleteness = "92",
            MinimumValidity = "93",
            MinimumUniqueness = "94",
            MinimumConsistency = "95",
            MinimumAnomalyControl = "96"
        };

        var success = viewModel.TryBuildBaseline(out var baseline, out var error);

        Assert.True(success);
        Assert.Null(error);
        Assert.NotNull(baseline);
        Assert.Equal("sample", baseline!.SourceName);
        Assert.Equal(91.5, baseline.MinimumOverallScore);
        Assert.Equal(96, baseline.MinimumAnomalyControl);
    }

    [Fact]
    public void Output_appends_messages_without_losing_previous_content()
    {
        var viewModel = new MainViewModel();

        viewModel.AppendOutput("第一行");
        viewModel.AppendOutput("第二行");

        Assert.Equal("第一行\r\n第二行", viewModel.OutputText);
    }

    [Fact]
    public void GetDeepAnalysis_marks_missing_result_as_not_generated()
    {
        var text = MainViewModel.GetDeepAnalysis("CorrelationMatrix", null);

        Assert.Contains("未生成", text, StringComparison.Ordinal);
        Assert.Contains("CorrelationMatrix", text, StringComparison.Ordinal);
    }

    private static AnalysisResult CreateResult()
    {
        var field = new FieldStatistic(
            "Id",
            10,
            1,
            0,
            9,
            "Int64",
            0.9,
            null);

        return new AnalysisResult
        {
            TotalObjects = 10,
            TotalUniqueObjects = 9,
            TotalDuplicateGroups = 1,
            FieldStatistics = [field],
            QualityViolations = []
        };
    }
}