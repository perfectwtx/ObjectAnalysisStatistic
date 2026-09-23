using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ObjectAnalyzer.Desktop.Services;
using ObjectAnalyzer.Desktop.ViewModels;

namespace ObjectAnalyzer.Desktop;

public partial class MainWindow : Window
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".json", ".jsonl", ".csv", ".xlsx", ".xls", ".xml", ".yaml", ".yml", ".gz", ".bz2", ".zip"
    };

    private readonly MainViewModel _viewModel = new();
    private readonly DesktopPlatformService _platformService = new();
    private CancellationTokenSource? _analysisCancellation;
    private bool _suppressPreferenceEvents;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;

        QualityViewControl.LoadBaselineRequested += async (_, _) => await LoadBaselineFromViewAsync();
        QualityViewControl.SaveBaselineRequested += async (_, _) => await SaveBaselineFromViewAsync();
        QualityViewControl.DeleteBaselineRequested += async (_, _) => await DeleteBaselineFromViewAsync();
        JobsViewControl.RefreshRequested += async (_, _) => await RefreshJobsFromViewAsync();
        ReportsViewControl.RefreshRequested += (_, _) => RefreshReportHistory();
        ReportsViewControl.ReportLoadRequested += (_, e) => LoadReport(e.Path);

        Loaded += MainWindow_Loaded;
        LocalizationService.LanguageChanged += (_, _) => Dispatcher.Invoke(RefreshPreferenceControls);
        ThemeService.ThemeChanged += (_, _) => Dispatcher.Invoke(RefreshPreferenceControls);
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;
        try
        {
            RefreshPreferenceControls();
            await RefreshPlatformDataAsync();
            _viewModel.SetRecentFiles(RecentFilesService.Load());
            RefreshReportHistory();
            await LoadBaselineAsync();

            if (string.IsNullOrWhiteSpace(_viewModel.Status)
                || _viewModel.Status is "等待选择文件" or "Waiting for a file")
            {
                _viewModel.SetStatus(LocalizationService.Get("Str.Status.WaitingFile", "等待选择文件"));
            }
        }
        catch (Exception ex)
        {
            _viewModel.SetStatus($"本地历史初始化失败：{ex.Message}");
        }
    }

    private void RefreshPreferenceControls()
    {
        _suppressPreferenceEvents = true;
        try
        {
            LanguageComboBox.Items.Clear();
            LanguageComboBox.Items.Add(new ComboBoxItem
            {
                Content = LocalizationService.Get("Str.Lang.Zh", "中文"),
                Tag = AppLanguage.Chinese
            });
            LanguageComboBox.Items.Add(new ComboBoxItem
            {
                Content = LocalizationService.Get("Str.Lang.En", "English"),
                Tag = AppLanguage.English
            });
            LanguageComboBox.SelectedIndex = LocalizationService.Current == AppLanguage.English ? 1 : 0;

            ThemeComboBox.Items.Clear();
            foreach (AppThemeMode mode in Enum.GetValues<AppThemeMode>())
            {
                ThemeComboBox.Items.Add(new ComboBoxItem
                {
                    Content = ThemeService.GetModeLabel(mode),
                    Tag = mode
                });
            }

            for (int i = 0; i < ThemeComboBox.Items.Count; i++)
            {
                if (ThemeComboBox.Items[i] is ComboBoxItem item
                    && item.Tag is AppThemeMode mode
                    && mode == ThemeService.Mode)
                {
                    ThemeComboBox.SelectedIndex = i;
                    break;
                }
            }
        }
        finally
        {
            _suppressPreferenceEvents = false;
        }
    }

    private void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressPreferenceEvents)
            return;

        if (LanguageComboBox.SelectedItem is not ComboBoxItem item || item.Tag is not AppLanguage language)
            return;

        if (language == LocalizationService.Current)
            return;

        LocalizationService.Apply(language);
        RefreshPreferenceControls();
        _viewModel.SetStatus(LocalizationService.Get("Str.Status.LanguageApplied", "已切换语言"));
    }

    private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressPreferenceEvents)
            return;

        if (ThemeComboBox.SelectedItem is not ComboBoxItem item || item.Tag is not AppThemeMode mode)
            return;

        if (mode == ThemeService.Mode)
            return;

        ThemeService.Apply(mode);
        RefreshPreferenceControls();
        _viewModel.SetStatus(LocalizationService.Get("Str.Status.ThemeApplied", "已切换主题"));
    }

    private void SelectFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = LocalizationService.Get("Str.Dialog.SelectFileTitle", "选择要分析的数据文件"),
            Filter = "Supported|*.json;*.jsonl;*.csv;*.xlsx;*.xls;*.xml;*.yaml;*.yml;*.gz;*.bz2;*.zip|All|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) == true)
        {
            SetInputFile(
                dialog.FileName,
                LocalizationService.Get("Str.Status.FileSelected", "已选择文件，等待开始分析"));
        }
    }

    private void InputPathTextBox_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void InputPathTextBox_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return;

        var files = e.Data.GetData(DataFormats.FileDrop) as string[];
        string? path = files?.FirstOrDefault(IsSupportedFile);
        if (path is null)
        {
            MessageBox.Show(
                this,
                LocalizationService.Get("Str.Dialog.UnsupportedFile", "拖入的文件格式不受支持。"),
                LocalizationService.Get("Str.Dialog.CannotSelectFile", "无法选择文件"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        SetInputFile(
            path,
            LocalizationService.Get("Str.Status.FileDropped", "已通过拖拽选择文件，等待开始分析"));
    }

    private void RecentFiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox comboBox || comboBox.SelectedItem is not string path)
            return;

        comboBox.SelectedItem = null;
        if (!File.Exists(path))
        {
            _viewModel.SetRecentFiles(_viewModel.RecentFiles.Where(File.Exists));
            RecentFilesService.Save(_viewModel.RecentFiles);
            _viewModel.SetStatus("最近文件已失效，请重新选择文件");
            return;
        }

        SetInputFile(
            path,
            LocalizationService.Get("Str.Status.FileFromRecent", "已从最近文件载入，等待开始分析"));
    }

    private void SetInputFile(string path, string status)
    {
        if (!IsSupportedFile(path))
            return;

        _viewModel.InputPath = Path.GetFullPath(path);
        InputPathTextBox.ToolTip = _viewModel.InputPath;
        _viewModel.AddRecentFile(_viewModel.InputPath);
        RecentFilesService.Save(_viewModel.RecentFiles);
        _viewModel.SetStatus(status);
    }

    private static bool IsSupportedFile(string path) =>
        File.Exists(path) && SupportedExtensions.Contains(Path.GetExtension(path));

    private async void Analyze_Click(object sender, RoutedEventArgs e)
    {
        string inputPath = _viewModel.InputPath.Trim();
        if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
        {
            MessageBox.Show(
                this,
                LocalizationService.Get("Str.Dialog.NeedExistingFile", "请选择一个存在的数据文件。"),
                LocalizationService.Get("Str.Dialog.CannotStart", "无法开始分析"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        string reportPath = BuildReportPath(inputPath);
        _viewModel.SetRunning(reportPath);
        _viewModel.ClearOutput();
        SetBusy(true);

        using var cancellation = new CancellationTokenSource();
        _analysisCancellation = cancellation;

        try
        {
            var result = await LocalAnalysisService.AnalyzeAsync(
                inputPath,
                reportPath,
                message => Dispatcher.Invoke(() => _viewModel.AppendOutput(message)),
                value => Dispatcher.Invoke(() => _viewModel.SetProgress(value)),
                _platformService,
                cancellation.Token);

            if (result.ExitCode == 0 && result.Result is not null)
            {
                _viewModel.AddRecentFile(inputPath);
                RecentFilesService.Save(_viewModel.RecentFiles);
                _viewModel.SetCompleted(result.Result);
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
            _viewModel.AppendOutput($"错误: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_analysisCancellation, cancellation))
                _analysisCancellation = null;

            SetBusy(false);
            RefreshReportHistory();

            try
            {
                await RefreshPlatformDataAsync();
            }
            catch
            {
                // 主分析结果已经完成，历史刷新失败不阻断主流程。
            }
        }
    }

    private void CancelAnalysis_Click(object sender, RoutedEventArgs e)
    {
        if (_analysisCancellation is null)
            return;

        _viewModel.SetStatus("正在取消分析...");
        _analysisCancellation.Cancel();
    }

    private void OpenReport_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.HasReport || !File.Exists(_viewModel.ReportPath))
            return;

        Process.Start(new ProcessStartInfo
        {
            FileName = _viewModel.ReportPath,
            UseShellExecute = true
        });
    }

    private void LoadReport(string reportPath)
    {
        try
        {
            var result = LocalAnalysisService.LoadResult(reportPath);
            _viewModel.Load(result, reportPath);
            _viewModel.SetStatus($"已加载历史报告：{Path.GetFileName(reportPath)}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"报告读取失败：{ex.Message}", "无法打开报告",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task RefreshPlatformDataAsync()
    {
        await _platformService.InitializeAsync();
        var jobs = await _platformService.HistoryStore.GetRecentAsync(100);
        var snapshots = await _platformService.QualityHistoryQuery.GetSnapshotsAsync(null, 100);

        _viewModel.SetJobs(jobs);
        _viewModel.SetQualityTrend(snapshots);
    }

    private async Task RefreshJobsFromViewAsync()
    {
        try
        {
            await RefreshPlatformDataAsync();
            _viewModel.SetStatus("作业历史已刷新");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"作业历史读取失败：{ex.Message}", "刷新失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task LoadBaselineFromViewAsync()
    {
        try
        {
            await LoadBaselineAsync();
            _viewModel.SetStatus("质量基线已读取");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"质量基线读取失败：{ex.Message}", "读取失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task SaveBaselineFromViewAsync()
    {
        if (!_viewModel.TryBuildBaseline(out var baseline, out var error) || baseline is null)
        {
            MessageBox.Show(this, error, "无法保存质量基线",
                MessageBoxButton.OK, MessageBoxImage.Warning);
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
            MessageBox.Show(this, $"质量基线保存失败：{ex.Message}", "保存失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task DeleteBaselineFromViewAsync()
    {
        string sourceName = ResolveSourceName();
        if (string.IsNullOrWhiteSpace(sourceName))
        {
            MessageBox.Show(this, "请先指定数据源名称。", "无法删除",
                MessageBoxButton.OK, MessageBoxImage.Warning);
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
            MessageBox.Show(this, $"质量基线删除失败：{ex.Message}", "删除失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
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

    private void RefreshReportHistory()
    {
        _viewModel.ClearReports();
        string directory = GetReportDirectory();

        if (Directory.Exists(directory))
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*.json")
                         .OrderByDescending(File.GetLastWriteTime)
                         .Take(100))
            {
                _viewModel.AddReport(path);
            }
        }
    }

    private string ResolveSourceName()
    {
        string configured = _viewModel.BaselineSourceName.Trim();
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        string input = _viewModel.InputPath.Trim();
        return string.IsNullOrWhiteSpace(input) ? string.Empty : Path.GetFileName(input);
    }

    private static string BuildReportPath(string inputPath)
    {
        string directory = GetReportDirectory();
        Directory.CreateDirectory(directory);

        return Path.Combine(
            directory,
            $"{Path.GetFileNameWithoutExtension(inputPath)}-{DateTime.Now:yyyyMMdd-HHmmss}.json");
    }

    private static string GetReportDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ObjectAnalyzer",
            "Reports");

    private void SetBusy(bool busy)
    {
        AnalyzeButton.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
        InputPathTextBox.IsEnabled = !busy;
    }
}
