using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using ObjectAnalyzer.Desktop.Services;
using ObjectAnalyzer.Desktop.ViewModels;

namespace ObjectAnalyzer.Desktop.Views;

public partial class InsightsView : UserControl
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };
    private MainViewModel? _observed;
    public event EventHandler? OpenReportsRequested;

    public InsightsView()
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
    private void OpenReports_Click(object sender, RoutedEventArgs e) => OpenReportsRequested?.Invoke(this, EventArgs.Empty);

    private void Refresh()
    {
        if (ResultTextBox is null || Dimensions.SelectedItem is not ListBoxItem item) return;
        var result = (DataContext as MainViewModel)?.Result;
        string key = (string)item.Tag;
        ResultTitle.Text = item.Content?.ToString();
        ResultTextBox.Text = string.Empty;
        ResultTextBox.Visibility = RelationsGrid.Visibility = Visibility.Collapsed;
        RelationsGrid.ItemsSource = null;
        MissingPanel.Visibility = Visibility.Visible;
        MissingHint.Text = LocalizationService.Get(result is null ? "Str.Insights.NoResult" : "Str.Insights.Help." + key);
        var relationships = result?.ObjectRelationships;
        if (key == "Lineage" && relationships is { Count: > 0 })
        {
            RelationsGrid.ItemsSource = relationships.Select(r => new
            {
                r.Path, r.Kind, r.OccurrenceCount, r.NonNullCount,
                Targets = string.Join(", ", r.TargetFields) + (r.TargetFieldCount > r.TargetFields.Count ? " …" : "")
            }).ToList();
            MissingPanel.Visibility = Visibility.Collapsed;
            RelationsGrid.Visibility = Visibility.Visible;
        }
        else if (key == "Impact" && relationships is { Count: > 0 })
        {
            ShowEvaluation(new
            {
                Note = LocalizationService.Get("Str.Insights.ImpactNote"),
                Paths = relationships.OrderByDescending(r => r.TargetFieldCount).Select(r => new
                {
                    SourcePath = r.Path, r.TargetFieldCount, AvailableTargetFields = r.TargetFields,
                    TargetListTruncated = r.TargetFieldCount > r.TargetFields.Count
                })
            });
        }
        else if (key == "SchemaImpact" && result?.SchemaDiff is { } schema) ShowEvaluation(schema);
        else if (key == "Regression" && result?.DistributionDiff is { } distribution) ShowEvaluation(distribution);
    }

    public void ShowEvaluation(object evaluation)
    {
        MissingPanel.Visibility = RelationsGrid.Visibility = Visibility.Collapsed;
        ResultTextBox.Visibility = Visibility.Visible;
        ResultTextBox.Text = JsonSerializer.Serialize(evaluation, s_jsonOptions);
        ResultTextBox.ScrollToHome();
    }
}
