using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using ObjectAnalyzer.Desktop.Services;
using ObjectAnalyzer.Desktop.ViewModels;

namespace ObjectAnalyzer.Desktop;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();
    private readonly LocalAnalysisService _analysisService = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        RefreshReportHistory();
    }

    private void SelectFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择要分析的数据文件",
            Filter = "支持的文件|*.json;*.jsonl;*.csv;*.xlsx;*.xls;*.xml;*.yaml;*.yml;*.gz;*.bz2;*.zip|所有文件|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) == true)
        {
            _viewModel.InputPath = dialog.FileName;
            _viewModel.StatusText = "已选择文件，等待开始分析";
            StatusTextBlock.Text = _viewModel.StatusText;
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

        string reportPath = BuildReportPath(inputPath);
        _viewModel.SetRunning(reportPath);
        SetBusy(true);
        OutputTextBox.Clear();
        BindCollections();

        try
        {
            var result = await _analysisService.AnalyzeAsync(
                inputPath,
                reportPath,
                message => Dispatcher.Invoke(() =>
                {
                    OutputTextBox.AppendText(message + Environment.NewLine);
                    OutputTextBox.ScrollToEnd();
                }),
                value => Dispatcher.Invoke(() =>
                {
                    _viewModel.SetProgress(value);
                    AnalysisProgress.Value = _viewModel.Progress;
                }));

            if (result.ExitCode == 0 && result.Result is not null)
            {
                _viewModel.SetCompleted(result.Result);
                LoadResultView(reportPath);
            }
            else
            {
                _viewModel.SetFailed($"分析结束，退出码 {result.ExitCode}");
            }
        }
        catch (OperationCanceledException)
        {
            _viewModel.SetFailed("分析已取消");
        }
        catch (Exception ex)
        {
            _viewModel.SetFailed(ex.Message);
            OutputTextBox.AppendText($"错误: {ex.Message}{Environment.NewLine}");
        }
        finally
        {
            SetBusy(false);
            RefreshReportHistory();
        }
    }

    private void LoadReport(string reportPath)
    {
        try
        {
            var result = LocalAnalysisService.LoadResult(reportPath);
            _viewModel.Load(result, reportPath);
            LoadResultView(reportPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"报告读取失败：{ex.Message}", "无法打开报告", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LoadResultView(string reportPath)
    {
        ReportPathTextBlock.Text = reportPath;
        TotalObjectsText.Text = (_viewModel.Result?.TotalObjects ?? 0).ToString("N0");
        FieldCountText.Text = (_viewModel.Result?.FieldStatistics.Count ?? 0).ToString("N0");
        DuplicateGroupsText.Text = (_viewModel.Result?.TotalDuplicateGroups ?? 0).ToString("N0");
        QualityIssuesText.Text = (_viewModel.Result?.QualityViolations.Count ?? 0).ToString("N0");

        BindCollections();
        OpenReportButton.IsEnabled = File.Exists(reportPath);
        ExportCsvButton.IsEnabled = _viewModel.Result is not null;
        DeepAnalysisTextBox.Text = "请选择上方分析维度。\r\n\r\n未启用的深度分析会明确显示“未生成”。";
        InsightTextBox.Text = "请选择上方分析维度。\r\n\r\n当前页面基于本地 AnalysisResult 展示分析结果。";
    }

    private void BindCollections()
    {
        FieldGrid.ItemsSource = _viewModel.Fields;
        SchemaGrid.ItemsSource = _viewModel.Fields;
        DuplicateGrid.ItemsSource = _viewModel.Duplicates;
        IssueList.ItemsSource = _viewModel.Issues;
        ReportHistoryList.ItemsSource = _viewModel.Reports;
    }

    private void ShowCorrelation_Click(object sender, RoutedEventArgs e) => ShowDeep(nameof(ObjectAnalyzer.Analysis.AnalysisResult.CorrelationMatrix), _viewModel.Result?.CorrelationMatrix);
    private void ShowFuzzy_Click(object sender, RoutedEventArgs e) => ShowDeep(nameof(ObjectAnalyzer.Analysis.AnalysisResult.FuzzyDuplicates), _viewModel.Result?.FuzzyDuplicates);
    private void ShowPatterns_Click(object sender, RoutedEventArgs e) => ShowDeep(nameof(ObjectAnalyzer.Analysis.AnalysisResult.StringPatterns), _viewModel.Result?.StringPatterns);
    private void ShowUnicode_Click(object sender, RoutedEventArgs e) => ShowDeep(nameof(ObjectAnalyzer.Analysis.AnalysisResult.UnicodeHygiene), _viewModel.Result?.UnicodeHygiene);
    private void ShowTimeSeries_Click(object sender, RoutedEventArgs e) => ShowDeep(nameof(ObjectAnalyzer.Analysis.AnalysisResult.TimeSeriesAnalyses), _viewModel.Result?.TimeSeriesAnalyses);
    private void ShowDistribution_Click(object sender, RoutedEventArgs e) => ShowDeep(nameof(ObjectAnalyzer.Analysis.AnalysisResult.DistributionDiff), _viewModel.Result?.DistributionDiff);
    private void ShowSampling_Click(object sender, RoutedEventArgs e) => ShowDeep(nameof(ObjectAnalyzer.Analysis.AnalysisResult.SamplingConfidence), _viewModel.Result?.SamplingConfidence);

    private void ShowDeep(string name, object? value) => DeepAnalysisTextBox.Text = _viewModel.GetDeepAnalysis(name, value);

    private void ShowLineage_Click(object sender, RoutedEventArgs e)
    {
        var relationships = _viewModel.Result?.ObjectRelationships;
        InsightTextBox.Text = relationships is not { Count: > 0 }
            ? "数据血缘\r\n\r\n当前结果未生成对象关系。"
            : string.Join(Environment.NewLine, relationships.Select(x =>
                $"{x.Kind,-18} {x.Path}  出现 {x.OccurrenceCount:N0} 次，非空 {x.NonNullCount:N0}，目标字段 {string.Join(", ", x.TargetFields)}"));
    }

    private void ShowImpact_Click(object sender, RoutedEventArgs e)
    {
        var relationships = _viewModel.Result?.ObjectRelationships;
        InsightTextBox.Text = relationships is not { Count: > 0 }
            ? "影响分析\r\n\r\n当前结果未生成对象关系。"
            : "影响分析（基于当前本地结果）\r\n\r\n" + string.Join(Environment.NewLine,
                relationships.Select(x => $"• {x.Path} → {string.Join(", ", x.TargetFields)} | {x.Kind} | {x.OccurrenceCount:N0} 次"));
    }

    private void ShowSchemaImpact_Click(object sender, RoutedEventArgs e) =>
        InsightTextBox.Text = _viewModel.GetInsight("Schema Impact", _viewModel.Result?.SchemaDiff);

    private void ShowRegression_Click(object sender, RoutedEventArgs e) =>
        InsightTextBox.Text = _viewModel.GetInsight("质量回归 / 分布漂移", _viewModel.Result?.DistributionDiff);

    private void OpenReport_Click(object sender, RoutedEventArgs e)
    {
        string path = _viewModel.ReportPath;
        if (!File.Exists(path))
            return;

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{path}\"",
            UseShellExecute = true
        });
    }

    private void RefreshReports_Click(object sender, RoutedEventArgs e) => RefreshReportHistory();

    private void RefreshReportHistory()
    {
        _viewModel.ClearReports();
        string directory = GetReportDirectory();
        if (Directory.Exists(directory))
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*.json")
                         .OrderByDescending(File.GetLastWriteTime)
                         .Take(100))
                _viewModel.AddReport(path);
        }
        BindCollections();
    }

    private void ReportHistory_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ReportHistoryList.SelectedItem is DesktopReportRow row && File.Exists(row.Path))
            LoadReport(row.Path);
    }

    private void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        var result = _viewModel.Result;
        if (result is null)
            return;

        var dialog = new SaveFileDialog
        {
            Title = "导出字段 CSV",
            Filter = "CSV 文件|*.csv",
            FileName = "object-analyzer-fields.csv"
        };
        if (dialog.ShowDialog(this) != true)
            return;

        var sb = new StringBuilder("FieldName,PrimaryType,Coverage,Count,NullCount,DistinctCount\r\n");
        foreach (var field in result.FieldStatistics.OrderBy(x => x.FieldName, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append(EscapeCsv(field.FieldName)).Append(',')
              .Append(EscapeCsv(field.PrimaryType)).Append(',')
              .Append(field.Coverage.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',')
              .Append(field.Count).Append(',')
              .Append(field.NullCount).Append(',')
              .Append(field.DistinctCount?.ToString() ?? string.Empty).AppendLine();
        }

        File.WriteAllText(dialog.FileName, sb.ToString(), new UTF8Encoding(true));
        _viewModel.StatusText = $"CSV 已导出：{dialog.FileName}";
        StatusTextBlock.Text = _viewModel.StatusText;
    }

    private static string EscapeCsv(string value) =>
        value.IndexOfAny([',', '"', '\\n', '\\r']) < 0
            ? value
            : "\"" + value.Replace("\"", "\"\"") + "\"";

    private static string BuildReportPath(string inputPath)
    {
        string directory = GetReportDirectory();
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(inputPath)}-{DateTime.Now:yyyyMMdd-HHmmss}.json");
    }

    private static string GetReportDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ObjectAnalyzer", "Reports");

    private void SetBusy(bool busy)
    {
        AnalyzeButton.IsEnabled = !busy;
        InputPathTextBox.IsEnabled = !busy;
        FooterTextBlock.Text = busy
            ? "Object Analyzer Desktop · 正在执行本地分析"
            : "Object Analyzer Desktop · .NET 10 · WPF";
    }
}
