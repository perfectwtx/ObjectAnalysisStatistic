using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using ObjectAnalyzer.Desktop.Services;
using ObjectAnalyzer.Desktop.ViewModels;
using ObjectAnalyzer.Platform;

namespace ObjectAnalyzer.Desktop;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();
    private readonly LocalAnalysisService _analysisService = new();
    private readonly DesktopPlatformService _platformService = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        BindCollections();
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;
        try
        {
            await RefreshPlatformDataAsync();
            RefreshReportHistory();
            await LoadBaselineAsync();
        }
        catch (Exception ex)
        {
            _viewModel.SetStatus($"本地历史初始化失败：{ex.Message}");
        }
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
            _viewModel.SetStatus("已选择文件，等待开始分析");
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
                }),
                _platformService);

            if (result.ExitCode == 0 && result.Result is not null)
            {
                _viewModel.SetCompleted(result.Result);
                LoadResultView(reportPath);
                await RefreshPlatformDataAsync();
                await LoadBaselineAsync();
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
        ReportDetailsTextBlock.Text = $"当前报告：{Path.GetFileName(reportPath)}";
        TotalObjectsText.Text = (_viewModel.Result?.TotalObjects ?? 0).ToString("N0");
        FieldCountText.Text = (_viewModel.Result?.FieldStatistics.Count ?? 0).ToString("N0");
        DuplicateGroupsText.Text = (_viewModel.Result?.TotalDuplicateGroups ?? 0).ToString("N0");
        QualityIssuesText.Text = (_viewModel.Result?.QualityViolations.Count ?? 0).ToString("N0");

        BindCollections();
        OpenReportButton.IsEnabled = File.Exists(reportPath);
        ExportCsvButton.IsEnabled = _viewModel.Result is not null;
        DuplicateSummaryText.Text = _viewModel.Result is null
            ? "暂无重复对象结果。"
            : $"重复对象组：{_viewModel.Result.TotalDuplicateGroups:N0}，唯一对象：{_viewModel.Result.TotalUniqueObjects:N0}";

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
        QualityTrendGrid.ItemsSource = _viewModel.QualityTrend;
        JobGrid.ItemsSource = _viewModel.Jobs;
    }

    private void ShowCorrelation_Click(object sender, RoutedEventArgs e) => ShowDeep(nameof(AnalysisResult.CorrelationMatrix), _viewModel.Result?.CorrelationMatrix);
    private void ShowFuzzy_Click(object sender, RoutedEventArgs e) => ShowDeep(nameof(AnalysisResult.FuzzyDuplicates), _viewModel.Result?.FuzzyDuplicates);
    private void ShowPatterns_Click(object sender, RoutedEventArgs e) => ShowDeep(nameof(AnalysisResult.StringPatterns), _viewModel.Result?.StringPatterns);
    private void ShowUnicode_Click(object sender, RoutedEventArgs e) => ShowDeep(nameof(AnalysisResult.UnicodeHygiene), _viewModel.Result?.UnicodeHygiene);
    private void ShowTimeSeries_Click(object sender, RoutedEventArgs e) => ShowDeep(nameof(AnalysisResult.TimeSeriesAnalyses), _viewModel.Result?.TimeSeriesAnalyses);
    private void ShowDistribution_Click(object sender, RoutedEventArgs e) => ShowDeep(nameof(AnalysisResult.DistributionDiff), _viewModel.Result?.DistributionDiff);
    private void ShowSampling_Click(object sender, RoutedEventArgs e) => ShowDeep(nameof(AnalysisResult.SamplingConfidence), _viewModel.Result?.SamplingConfidence);

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

    private async Task RefreshPlatformDataAsync()
    {
        await _platformService.InitializeAsync();
        var jobs = await _platformService.HistoryStore.GetRecentAsync(100);
        var snapshots = await _platformService.QualityHistoryQuery.GetSnapshotsAsync(null, 100);

        _viewModel.SetJobs(jobs);
        _viewModel.SetQualityTrend(snapshots);
        BindCollections();
    }

    private async void RefreshJobs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await RefreshPlatformDataAsync();
            _viewModel.SetStatus("作业历史已刷新");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"作业历史读取失败：{ex.Message}", "刷新失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void LoadBaseline_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await LoadBaselineAsync();
            _viewModel.SetStatus("质量基线已读取");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"质量基线读取失败：{ex.Message}", "读取失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task LoadBaselineAsync()
    {
        await _platformService.InitializeAsync();
        string sourceName = ResolveSourceName();
        if (string.IsNullOrWhiteSpace(sourceName))
            return;

        var baseline = await _platformService.BaselineStore.GetAsync(sourceName);
        _viewModel.SetBaseline(baseline, sourceName);
    }

    private async void SaveBaseline_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.TryBuildBaseline(out var baseline, out var error) || baseline is null)
        {
            MessageBox.Show(this, error, "无法保存质量基线", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            await _platformService.InitializeAsync();
            await _platformService.BaselineStore.SaveAsync(baseline);
            _viewModel.SetStatus($"质量基线已保存：{baseline.SourceName}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"质量基线保存失败：{ex.Message}", "保存失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void DeleteBaseline_Click(object sender, RoutedEventArgs e)
    {
        string sourceName = ResolveSourceName();
        if (string.IsNullOrWhiteSpace(sourceName))
        {
            MessageBox.Show(this, "请先指定数据源名称。", "无法删除", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            await _platformService.InitializeAsync();
            await _platformService.BaselineStore.DeleteAsync(sourceName);
            _viewModel.SetBaseline(null, sourceName);
            _viewModel.SetStatus($"质量基线已删除：{sourceName}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"质量基线删除失败：{ex.Message}", "删除失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void EvaluateBaseline_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Result is null)
        {
            MessageBox.Show(this, "当前没有可评估的分析结果，请先完成一次分析或打开历史报告。", "无法评估", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!_viewModel.TryBuildBaseline(out var baseline, out var error) || baseline is null)
        {
            MessageBox.Show(this, error, "无法评估质量基线", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var evaluation = QualityBaselineEvaluator.Evaluate(
            baseline,
            QualityMetricsCalculator.Calculate(_viewModel.Result));

        InsightTextBox.Text = JsonSerializer.Serialize(
            evaluation,
            new JsonSerializerOptions { WriteIndented = true });

        _viewModel.SetStatus(evaluation.Passed
            ? "当前分析结果通过质量基线"
            : $"当前分析结果未通过质量基线，共 {evaluation.Failures.Count} 项");
    }

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

    private void ReportHistory_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReportHistoryList.SelectedItem is DesktopReportRow row)
            ReportDetailsTextBlock.Text = _viewModel.BuildReportDetails(row);
    }

    private void ReportHistory_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ReportHistoryList.SelectedItem is DesktopReportRow row && File.Exists(row.Path))
            LoadReport(row.Path);
    }

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
        _viewModel.SetStatus($"CSV 已导出：{dialog.FileName}");
    }

    private static string EscapeCsv(string value) =>
        value.IndexOfAny([',', '"', '\n', '\r']) < 0
            ? value
            : "\"" + value.Replace("\"", "\"\"") + "\"";

    private string ResolveSourceName()
    {
        string configured = _viewModel.BaselineSourceName.Trim();
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        string input = InputPathTextBox.Text.Trim();
        return string.IsNullOrWhiteSpace(input) ? string.Empty : Path.GetFileName(input);
    }

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
            : _viewModel.Status;
    }
}
