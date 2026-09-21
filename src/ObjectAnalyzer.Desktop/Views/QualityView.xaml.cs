using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using ObjectAnalyzer.Desktop.ViewModels;
using ObjectAnalyzer.Platform;

namespace ObjectAnalyzer.Desktop.Views;

public partial class QualityView : UserControl
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    public event EventHandler? LoadBaselineRequested;
    public event EventHandler? SaveBaselineRequested;
    public event EventHandler? DeleteBaselineRequested;

    public QualityView()
    {
        InitializeComponent();
    }

    private void LoadBaseline_Click(object sender, RoutedEventArgs e) =>
        LoadBaselineRequested?.Invoke(this, EventArgs.Empty);

    private void SaveBaseline_Click(object sender, RoutedEventArgs e) =>
        SaveBaselineRequested?.Invoke(this, EventArgs.Empty);

    private void DeleteBaseline_Click(object sender, RoutedEventArgs e) =>
        DeleteBaselineRequested?.Invoke(this, EventArgs.Empty);

    private void EvaluateBaseline_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel || viewModel.Result is null)
        {
            EvaluationTextBox.Text = "当前没有可评估的分析结果，请先完成一次分析或打开历史报告。";
            return;
        }

        if (!viewModel.TryBuildBaseline(out var baseline, out var error) || baseline is null)
        {
            EvaluationTextBox.Text = error;
            return;
        }

        var evaluation = QualityBaselineEvaluator.Evaluate(
            baseline,
            QualityMetricsCalculator.Calculate(viewModel.Result));

        EvaluationTextBox.Text = JsonSerializer.Serialize(
            evaluation,
            s_jsonOptions);

        viewModel.SetStatus(evaluation.Passed
            ? "当前分析结果通过质量基线"
            : $"当前分析结果未通过质量基线，共 {evaluation.Failures.Count} 项");
    }
}