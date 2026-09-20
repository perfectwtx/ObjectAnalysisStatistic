using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using ObjectAnalyzer.Abstractions;
using ObjectAnalyzer.Analysis;

namespace ObjectAnalyzer.Desktop;

public partial class MainWindow : Window
{
    private string? _reportPath;

    public MainWindow()
    {
        InitializeComponent();
    }

    private void SelectFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择要分析的数据文件",
            Filter = "支持的文件|*.json;*.jsonl;*.csv;*.xlsx;*.xls;*.xml;*.yaml;*.yml;*.gz;*.bz2;*.zip|所有文件|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) == true)
        {
            InputPathTextBox.Text = dialog.FileName;
            StatusTextBlock.Text = "已选择文件，等待开始分析";
        }
    }

    private async void Analyze_Click(object sender, RoutedEventArgs e)
    {
        string inputPath = InputPathTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
        {
            MessageBox.Show(this, "请选择一个存在的数据文件。", "无法开始分析", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SetBusy(true);
        OutputTextBox.Clear();
        AnalysisProgress.Value = 0;
        StatusTextBlock.Text = "正在分析...";
        _reportPath = BuildReportPath(inputPath);
        ReportPathTextBlock.Text = _reportPath;

        var console = new DesktopConsole(message =>
            Dispatcher.Invoke(() =>
            {
                OutputTextBox.AppendText(message + Environment.NewLine);
                OutputTextBox.ScrollToEnd();
            }));

        var progress = new DesktopProgressReporter(value =>
            Dispatcher.Invoke(() =>
            {
                AnalysisProgress.Value = Math.Clamp(value, 0, 100);
            }));

        AnalysisServices.Use(new AnalysisServices
        {
            Console = console,
            Progress = progress,
            Presenter = NullResultPresenter.Instance
        });

        try
        {
            var options = new AnalysisOptions
            {
                Input = new ObjectAnalyzer.Analysis.Options.InputSourceOptions
                {
                    InputPath = inputPath
                },
                Report = new ObjectAnalyzer.Analysis.Options.ReportOutputOptions
                {
                    OutputPath = _reportPath
                },
                Progress = new ObjectAnalyzer.Analysis.Options.ProgressOptions
                {
                    ShowProgress = true
                }
            };

            int exitCode = await Task.Run(() => AnalysisRunner.Run(options));

            StatusTextBlock.Text = exitCode == 0
                ? "分析完成"
                : $"分析结束，退出码 {exitCode}";

            if (exitCode == 0 && File.Exists(_reportPath))
            {
                LoadResult(_reportPath);
            }
            AnalysisProgress.Value = 100;
            OpenReportButton.IsEnabled = exitCode == 0 && File.Exists(_reportPath);
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = "分析失败";
            OutputTextBox.AppendText($"错误: {ex.Message}{Environment.NewLine}");
        }
        finally
        {
            AnalysisServices.Use(null);
            SetBusy(false);
        }
    }

    private void LoadResult(string reportPath)
    {
        try
        {
            var json = File.ReadAllText(reportPath);
            var result = JsonSerializer.Deserialize<AnalysisResult>(json);
            if (result is null)
                return;

            TotalObjectsText.Text = result.TotalObjects.ToString("N0");
            FieldCountText.Text = result.FieldStatistics.Count.ToString("N0");
            DuplicateGroupsText.Text = result.TotalDuplicateGroups.ToString("N0");
            QualityIssuesText.Text = result.QualityViolations.Count.ToString("N0");

            FieldGrid.ItemsSource = result.FieldStatistics
                .OrderBy(x => x.FieldName, StringComparer.OrdinalIgnoreCase)
                .Select(x => new DesktopFieldRow(x))
                .ToList();

            IssueList.ItemsSource = result.QualityViolations
                .Select(x => new DesktopIssueRow(x))
                .ToList();
        }
        catch (Exception ex)
        {
            OutputTextBox.AppendText($"报告读取失败: {ex.Message}{Environment.NewLine}");
        }
    }

    private void OpenReport_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_reportPath) || !File.Exists(_reportPath))
            return;

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,"{_reportPath}"",
            UseShellExecute = true
        });
    }

    private static string BuildReportPath(string inputPath)
    {
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ObjectAnalyzer",
            "Reports");
        Directory.CreateDirectory(directory);

        string name = Path.GetFileNameWithoutExtension(inputPath);
        return Path.Combine(directory, $"{name}-{DateTime.Now:yyyyMMdd-HHmmss}.json");
    }

    private void SetBusy(bool busy)
    {
        AnalyzeButton.IsEnabled = !busy;
        InputPathTextBox.IsEnabled = !busy;
        FooterTextBlock.Text = busy
            ? "Object Analyzer Desktop · 正在执行本地分析"
            : "Object Analyzer Desktop · .NET 10 · WPF";
    }
}

internal sealed class DesktopConsole : IAnalysisConsole
{
    private readonly Action<string> _write;

    public DesktopConsole(Action<string> write) => _write = write;

    public void MarkupLine(string markup) => _write(StripMarkup(markup));
    public void WriteLine(string text = "") => _write(text);
    public void Write(string text) => _write(text);

    private static string StripMarkup(string value)
    {
        return System.Text.RegularExpressions.Regex.Replace(value, @"\[/?[a-zA-Z0-9 #;,.:-]+\]", string.Empty);
    }
}

internal sealed class DesktopProgressReporter : IProgressReporter
{
    private readonly Action<long> _report;
    private long _lastValue;

    public DesktopProgressReporter(Action<long> report) => _report = report;

    public void Run(string description, long? maxObjects, bool showProgress, Action<IProgress<long>?> work)
    {
        var progress = new Progress<long>(value =>
        {
            _lastValue = value;
            if (maxObjects is > 0)
                _report((long)((double)value / maxObjects.Value * 100));
        });

        work(showProgress ? progress : null);

        if (maxObjects is null || maxObjects <= 0)
            _report(_lastValue > 0 ? 100 : 0);
    }
}


internal sealed class DesktopFieldRow
{
    public string FieldName { get; }
    public string PrimaryType { get; }
    public string CoverageText { get; }
    public long Count { get; }
    public long NullCount { get; }
    public long? DistinctCount { get; }

    public DesktopFieldRow(FieldStatistic source)
    {
        FieldName = source.FieldName;
        PrimaryType = source.PrimaryType;
        CoverageText = $"{source.Coverage:P1}";
        Count = source.Count;
        NullCount = source.NullCount;
        DistinctCount = source.DistinctCount;
    }
}

internal sealed class DesktopIssueRow
{
    public string FieldName { get; }
    public string Check { get; }
    public long Count { get; }
    public string Message { get; }

    public DesktopIssueRow(FieldViolation source)
    {
        FieldName = source.Field;
        Check = source.Check;
        Count = source.Count;
        Message = source.Message;
    }
}
