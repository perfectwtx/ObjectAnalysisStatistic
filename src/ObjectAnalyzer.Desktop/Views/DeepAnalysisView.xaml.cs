using System.Windows;
using System.Windows.Controls;
using ObjectAnalyzer.Analysis;
using ObjectAnalyzer.Desktop.ViewModels;

namespace ObjectAnalyzer.Desktop.Views;

public partial class DeepAnalysisView : UserControl
{
    public DeepAnalysisView()
    {
        InitializeComponent();
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private void ShowCorrelation_Click(object sender, RoutedEventArgs e) =>
        Show(nameof(AnalysisResult.CorrelationMatrix), ViewModel?.Result?.CorrelationMatrix);

    private void ShowFuzzy_Click(object sender, RoutedEventArgs e) =>
        Show(nameof(AnalysisResult.FuzzyDuplicates), ViewModel?.Result?.FuzzyDuplicates);

    private void ShowPatterns_Click(object sender, RoutedEventArgs e) =>
        Show(nameof(AnalysisResult.StringPatterns), ViewModel?.Result?.StringPatterns);

    private void ShowUnicode_Click(object sender, RoutedEventArgs e) =>
        Show(nameof(AnalysisResult.UnicodeHygiene), ViewModel?.Result?.UnicodeHygiene);

    private void ShowTimeSeries_Click(object sender, RoutedEventArgs e) =>
        Show(nameof(AnalysisResult.TimeSeriesAnalyses), ViewModel?.Result?.TimeSeriesAnalyses);

    private void ShowDistribution_Click(object sender, RoutedEventArgs e) =>
        Show(nameof(AnalysisResult.DistributionDiff), ViewModel?.Result?.DistributionDiff);

    private void ShowSampling_Click(object sender, RoutedEventArgs e) =>
        Show(nameof(AnalysisResult.SamplingConfidence), ViewModel?.Result?.SamplingConfidence);

    private void Show(string name, object? value) =>
        ResultTextBox.Text = MainViewModel.GetDeepAnalysis(name, value);
}