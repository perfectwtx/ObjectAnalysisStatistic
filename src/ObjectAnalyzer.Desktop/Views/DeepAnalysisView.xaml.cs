using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using ObjectAnalyzer.Desktop.Services;
using ObjectAnalyzer.Desktop.ViewModels;

namespace ObjectAnalyzer.Desktop.Views;

public partial class DeepAnalysisView : UserControl
{
    public event EventHandler? ConfigureRequested;
    private MainViewModel? _observed;

    public DeepAnalysisView()
    {
        InitializeComponent();
        Loaded += (_, _) => { Observe(); Refresh(); };
        Unloaded += (_, _) => StopObserving();
        DataContextChanged += (_, _) => { if (IsLoaded) { Observe(); Refresh(); } };
        Dimensions.SelectedIndex = 0;
    }

    private void Observe()
    {
        StopObserving();
        _observed = DataContext as MainViewModel;
        if (_observed is not null)
            PropertyChangedEventManager.AddHandler(_observed, ResultChanged, nameof(MainViewModel.Result));
    }

    private void StopObserving()
    {
        if (_observed is not null)
            PropertyChangedEventManager.RemoveHandler(_observed, ResultChanged, nameof(MainViewModel.Result));
        _observed = null;
    }

    private void ResultChanged(object? sender, PropertyChangedEventArgs e) => Refresh();
    private void Dimension_Changed(object sender, SelectionChangedEventArgs e) => Refresh();
    private void Configure_Click(object sender, RoutedEventArgs e) => ConfigureRequested?.Invoke(this, EventArgs.Empty);

    private void Refresh()
    {
        if (ResultTextBox is null || Dimensions.SelectedItem is not ListBoxItem item) return;
        var result = (DataContext as MainViewModel)?.Result;
        string key = (string)item.Tag;
        object? value = key switch
        {
            "Correlation" => result?.CorrelationMatrix,
            "Fuzzy" => result?.FuzzyDuplicates,
            "Patterns" => result?.StringPatterns,
            "Unicode" => result?.UnicodeHygiene,
            "TimeSeries" => result?.TimeSeriesAnalyses,
            "Distribution" => result?.DistributionDiff,
            "Sampling" => result?.SamplingConfidence,
            _ => null
        };
        ResultTitle.Text = item.Content?.ToString();
        MissingHint.Text = LocalizationService.Get("Str.Deep.Help." + key);
        MissingPanel.Visibility = value is null ? Visibility.Visible : Visibility.Collapsed;
        ConfigureButton.Visibility = key == "Distribution" ? Visibility.Collapsed : Visibility.Visible;
        ResultTextBox.Visibility = value is null ? Visibility.Collapsed : Visibility.Visible;
        ResultTextBox.Text = value is null ? string.Empty : MainViewModel.GetDeepAnalysis(key, value);
        ResultTextBox.ScrollToHome();
    }
}
