using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ObjectAnalyzer.Desktop.ViewModels;

namespace ObjectAnalyzer.Desktop.Views;

public partial class JobsView : UserControl
{
    public event EventHandler? RefreshRequested;

    public JobsView()
    {
        InitializeComponent();
    }

    private void RefreshJobs_Click(object sender, RoutedEventArgs e) =>
        RefreshRequested?.Invoke(this, EventArgs.Empty);

    private void JobGrid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (JobGrid.SelectedItem is not DesktopJobRow row)
            return;

        JobDetailsTextBlock.Text =
            $"作业 ID：{row.Id}\r\n"
            + $"数据源：{row.SourceName}（{row.SourceType}）\r\n"
            + $"状态：{row.StatusText}\r\n"
            + $"开始：{row.StartedText}，耗时：{row.DurationText}\r\n"
            + $"对象：{row.TotalObjects:N0}，字段：{row.FieldCount:N0}，质量分：{row.QualityScoreText}，问题：{row.IssueCount:N0}\r\n"
            + (string.IsNullOrWhiteSpace(row.ErrorMessage) ? "错误：无" : $"错误：{row.ErrorMessage}");
    }
}