using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using ObjectAnalyzer.Desktop.ViewModels;

namespace ObjectAnalyzer.Desktop.Views;

public partial class ReportsView : UserControl
{
    public event EventHandler? RefreshRequested;
    public event EventHandler<ReportLoadRequestedEventArgs>? ReportLoadRequested;

    public ReportsView()
    {
        InitializeComponent();
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private void RefreshReports_Click(object sender, RoutedEventArgs e) =>
        RefreshRequested?.Invoke(this, EventArgs.Empty);

    private void OpenReport_Click(object sender, RoutedEventArgs e)
    {
        string path = ViewModel?.ReportPath ?? string.Empty;
        if (!File.Exists(path))
            return;

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{path}\"",
            UseShellExecute = true
        });
    }

    private void ReportHistory_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReportHistoryList.SelectedItem is DesktopReportRow row)
            ReportDetailsTextBlock.Text = MainViewModel.BuildReportDetails(row);
    }

    private void ReportHistory_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ReportHistoryList.SelectedItem is DesktopReportRow row && File.Exists(row.Path))
            ReportLoadRequested?.Invoke(this, new ReportLoadRequestedEventArgs(row.Path));
    }

    private void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        var result = ViewModel?.Result;
        if (result is null)
            return;

        var dialog = new SaveFileDialog
        {
            Title = "导出字段 CSV",
            Filter = "CSV 文件|*.csv",
            FileName = "object-analyzer-fields.csv"
        };

        if (dialog.ShowDialog() != true)
            return;

        var sb = new StringBuilder("FieldName,PrimaryType,Coverage,Count,NullCount,DistinctCount\r\n");
        foreach (var field in result.FieldStatistics.OrderBy(x => x.FieldName, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append(EscapeCsv(field.FieldName)).Append(',')
              .Append(EscapeCsv(field.PrimaryType)).Append(',')
              .Append(field.Coverage.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(field.Count).Append(',')
              .Append(field.NullCount).Append(',')
              .Append(field.DistinctCount?.ToString(CultureInfo.InvariantCulture) ?? string.Empty)
              .AppendLine();
        }

        File.WriteAllText(dialog.FileName, sb.ToString(), new UTF8Encoding(true));
        ViewModel?.SetStatus($"CSV 已导出：{dialog.FileName}");
    }

    private static string EscapeCsv(string value) =>
        value.IndexOfAny([',', '"', '\n', '\r']) < 0
            ? value
            : "\"" + value.Replace("\"", "\"\"") + "\"";
}

public sealed class ReportLoadRequestedEventArgs : EventArgs
{
    public string Path { get; }

    public ReportLoadRequestedEventArgs(string path)
    {
        Path = path;
    }
}