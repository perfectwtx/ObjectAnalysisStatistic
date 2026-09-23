using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ObjectAnalyzer.Desktop.Assets;
using ObjectAnalyzer.Desktop.Services;
using ObjectAnalyzer.Desktop.ViewModels;
using ObjectAnalyzer.Desktop.Views;
using Wpf.Ui.Controls;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace ObjectAnalyzer.Desktop;

public partial class MainWindow : FluentWindow
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".json", ".jsonl", ".csv", ".xlsx", ".xls", ".xml", ".yaml", ".yml", ".gz", ".bz2", ".zip"
    };

    private readonly MainViewModel _viewModel = new();
    private readonly DesktopPlatformService _platformService = new();
    private readonly Dictionary<string, FrameworkElement> _pages = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _analysisCancellation;
    private bool _navWired;

    private AnalyzeView AnalyzeViewControl => (AnalyzeView)_pages["analyze"];
    private QualityView QualityViewControl => (QualityView)_pages["quality"];
    private JobsView JobsViewControl => (JobsView)_pages["jobs"];
    private ReportsView ReportsViewControl => (ReportsView)_pages["reports"];

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;

        if (AppIcon.Load() is { } icon)
            Icon = icon;
        AppIcon.EnsureFileBesideAssembly();

        BuildPages();

        AnalyzeViewControl.SelectFileRequested += (_, _) => SelectFile();
        AnalyzeViewControl.SelectRulesRequested += (_, _) => SelectRules();
        AnalyzeViewControl.AnalyzeRequested += async (_, _) => await RunAnalysisAsync();
        AnalyzeViewControl.CancelRequested += (_, _) => CancelAnalysis();
        AnalyzeViewControl.OpenReportRequested += (_, _) => OpenReport();
        AnalyzeViewControl.RecentFileSelected += (_, path) => OnRecentFileSelected(path);
        AnalyzeViewControl.ReportFormatChanged += (_, format) => _viewModel.ReportFormat = format;
        AnalyzeViewControl.FileDragOver += (_, e) => OnFileDragOver(e);
        AnalyzeViewControl.FileDrop += (_, e) => OnFileDrop(e);

        QualityViewControl.LoadBaselineRequested += async (_, _) => await LoadBaselineFromViewAsync();
        QualityViewControl.SaveBaselineRequested += async (_, _) => await SaveBaselineFromViewAsync();
        QualityViewControl.DeleteBaselineRequested += async (_, _) => await DeleteBaselineFromViewAsync();
        JobsViewControl.RefreshRequested += async (_, _) => await RefreshJobsFromViewAsync();
        ReportsViewControl.RefreshRequested += (_, _) => RefreshReportHistory();
        ReportsViewControl.ReportLoadRequested += (_, e) => LoadReport(e.Path);

        Loaded += MainWindow_Loaded;
    }

    private void BuildPages()
    {
        _pages["analyze"] = CreatePage(new AnalyzeView());
        _pages["overview"] = CreatePage(new OverviewView());
        _pages["schema"] = CreatePage(new SchemaView());
        _pages["duplicates"] = CreatePage(new DuplicateView());
        _pages["deep"] = CreatePage(new DeepAnalysisView());
        _pages["insights"] = CreatePage(new InsightsView());
        _pages["output"] = CreatePage(new OutputView());
        _pages["issues"] = CreatePage(new QualityIssuesView());
        _pages["quality"] = CreatePage(new QualityView());
        _pages["jobs"] = CreatePage(new JobsView());
        _pages["reports"] = CreatePage(new ReportsView());
        _pages["workspace"] = CreatePage(new WorkspaceView());
        _pages["settings"] = CreatePage(new SettingsView());
    }

    private FrameworkElement CreatePage(FrameworkElement view)
    {
        view.DataContext = _viewModel;
        return view;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;
        try
        {
            WireNavigation();
            NavigateTo("analyze");
            NavAnalyze.IsActive = true;

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

    private void WireNavigation()
    {
        if (_navWired)
            return;

        _navWired = true;

        foreach (object raw in RootNavigation.MenuItems)
        {
            if (raw is not NavigationViewItem item)
                continue;

            item.Click += (_, _) =>
            {
                if (item.Tag is string tag)
                    NavigateTo(tag);
            };
        }
    }

    private void NavigateTo(string tag)
    {
        if (!_pages.TryGetValue(tag, out FrameworkElement? page))
            return;

        PageHost.Content = page;

        foreach (object raw in RootNavigation.MenuItems)
        {
            if (raw is not NavigationViewItem item)
                continue;

            bool active = item.Tag is string itemTag
                && string.Equals(itemTag, tag, StringComparison.OrdinalIgnoreCase);
            if (active)
                item.Activate(RootNavigation);
            else
                item.Deactivate(RootNavigation);
        }
    }

    private void SelectFile()
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

    private void SelectRules()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择规则文件",
            Filter = "Rules|*.json;*.jsonc;*.yaml;*.yml|All|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) == true)
            _viewModel.RulesPath = Path.GetFullPath(dialog.FileName);
    }

    private static void OnFileDragOver(DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnFileDrop(DragEventArgs e)
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

    private void OnRecentFileSelected(string path)
    {
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
        AnalyzeViewControl.SetInputPathTooltip(_viewModel.InputPath);
        _viewModel.AddRecentFile(_viewModel.InputPath);
        RecentFilesService.Save(_viewModel.RecentFiles);
        _viewModel.SetStatus(status);
    }

    private static bool IsSupportedFile(string path) =>
        File.Exists(path) && SupportedExtensions.Contains(Path.GetExtension(path));

    private async Task RunAnalysisAsync()
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

        string? rulesPath = string.IsNullOrWhiteSpace(_viewModel.RulesPath)
            ? null
            : _viewModel.RulesPath.Trim();
        if (rulesPath is not null && !File.Exists(rulesPath))
        {
            MessageBox.Show(this, "规则文件不存在，请重新选择。", "无法开始分析",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 工作台概览依赖 JSON AnalysisResult；始终写入 .json。
        string reportPath = BuildReportPath(inputPath, "json");
        _viewModel.SetRunning(reportPath);
        _viewModel.ClearOutput();
        AnalyzeViewControl.SetBusy(true);

        using var cancellation = new CancellationTokenSource();
        _analysisCancellation = cancellation;

        try
        {
            var result = await LocalAnalysisService.AnalyzeAsync(
                inputPath,
                reportPath,
                rulesPath,
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
                NavigateTo("overview");
            }
            else if (result.ExitCode == 0)
            {
                _viewModel.SetStatus("分析完成（非 JSON 报告，工作台字段统计可能为空）");
                _viewModel.SetProgress(100);
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

            AnalyzeViewControl.SetBusy(false);
            RefreshReportHistory();

            try
            {
                await RefreshPlatformDataAsync();
            }
            catch
            {
            }
        }
    }

    private void CancelAnalysis()
    {
        if (_analysisCancellation is null)
            return;

        _viewModel.SetStatus("正在取消分析...");
        _analysisCancellation.Cancel();
    }

    private void OpenReport()
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
            if (!reportPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, "工作台仅支持回读 JSON 分析报告。", "无法打开报告",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

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
            foreach (var path in Directory.EnumerateFiles(directory, "*.*")
                         .Where(p =>
                         {
                             string ext = Path.GetExtension(p).ToLowerInvariant();
                             return ext is ".json" or ".html" or ".md" or ".csv" or ".xlsx" or ".pdf";
                         })
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

    private static string BuildReportPath(string inputPath, string format)
    {
        string directory = GetReportDirectory();
        Directory.CreateDirectory(directory);

        string ext = format?.Trim().ToLowerInvariant() switch
        {
            "html" => ".html",
            "md" or "markdown" => ".md",
            "csv" => ".csv",
            "xlsx" or "excel" => ".xlsx",
            "pdf" => ".pdf",
            _ => ".json"
        };

        return Path.Combine(
            directory,
            $"{Path.GetFileNameWithoutExtension(inputPath)}-{DateTime.Now:yyyyMMdd-HHmmss}{ext}");
    }

    private static string GetReportDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ObjectAnalyzer",
            "Reports");
}
