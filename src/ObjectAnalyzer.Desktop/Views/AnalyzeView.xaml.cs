using System.Windows;
using System.Windows.Controls;

namespace ObjectAnalyzer.Desktop.Views;

public partial class AnalyzeView : UserControl
{
    public event EventHandler? SelectFileRequested;
    public event EventHandler? SelectRulesRequested;
    public event EventHandler? ConfigureRulesRequested;
    public event EventHandler? AnalyzeRequested;
    public event EventHandler? CancelRequested;
    public event EventHandler? OpenReportRequested;
    public event EventHandler? OpenReportFolderRequested;
    public event EventHandler<string>? RecentFileSelected;
    public event EventHandler<string>? ReportFormatChanged;
    public event EventHandler<DragEventArgs>? FileDragOver;
    public event EventHandler<DragEventArgs>? FileDrop;

    public AnalyzeView()
    {
        InitializeComponent();
    }

    public void SetBusy(bool busy)
    {
        AnalyzeButton.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
        InputPathTextBox.IsEnabled = !busy;
        RulesPathTextBox.IsEnabled = !busy;
        ReportFormatComboBox.IsEnabled = !busy;
    }

    public void SetInputPathTooltip(string path) => InputPathTextBox.ToolTip = path;

    private void SelectFile_Click(object sender, RoutedEventArgs e) =>
        SelectFileRequested?.Invoke(this, EventArgs.Empty);

    private void SelectRules_Click(object sender, RoutedEventArgs e) =>
        SelectRulesRequested?.Invoke(this, EventArgs.Empty);

    private void ConfigureRules_Click(object sender, RoutedEventArgs e) =>
        ConfigureRulesRequested?.Invoke(this, EventArgs.Empty);

    private void Analyze_Click(object sender, RoutedEventArgs e) =>
        AnalyzeRequested?.Invoke(this, EventArgs.Empty);

    private void CancelAnalysis_Click(object sender, RoutedEventArgs e) =>
        CancelRequested?.Invoke(this, EventArgs.Empty);

    private void OpenReport_Click(object sender, RoutedEventArgs e) =>
        OpenReportRequested?.Invoke(this, EventArgs.Empty);

    private void OpenReportFolder_Click(object sender, RoutedEventArgs e) =>
        OpenReportFolderRequested?.Invoke(this, EventArgs.Empty);

    private void RecentFiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox comboBox || comboBox.SelectedItem is not string path)
            return;

        comboBox.SelectedItem = null;
        RecentFileSelected?.Invoke(this, path);
    }

    private void ReportFormatComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReportFormatComboBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            ReportFormatChanged?.Invoke(this, tag);
    }

    private void InputPathTextBox_DragOver(object sender, DragEventArgs e) =>
        FileDragOver?.Invoke(this, e);

    private void InputPathTextBox_Drop(object sender, DragEventArgs e) =>
        FileDrop?.Invoke(this, e);
}
