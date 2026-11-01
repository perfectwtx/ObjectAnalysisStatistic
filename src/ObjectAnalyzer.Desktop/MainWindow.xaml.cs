using System.Globalization;
using System.Windows;
using System.Windows.Input;
using ObjectAnalyzer.Desktop.Assets;
using ObjectAnalyzer.Desktop.Services;
using ObjectAnalyzer.Desktop.ViewModels;
using ObjectAnalyzer.Desktop.Views;
using Wpf.Ui.Controls;

namespace ObjectAnalyzer.Desktop;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The WPF window releases its platform service in OnClosed.")]
public partial class MainWindow : FluentWindow
{
    private readonly MainViewModel _viewModel = new();
    private readonly DesktopPlatformService _platformService = new();
    private readonly Dictionary<string, FrameworkElement> _pages = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _analysisCancellation;
    private bool _navWired;

    private AnalyzeView AnalyzeViewControl => (AnalyzeView)_pages["analyze"];
    private QualityView QualityViewControl => (QualityView)_pages["quality"];
    private JobsView JobsViewControl => (JobsView)_pages["jobs"];
    private ReportsView ReportsViewControl => (ReportsView)_pages["reports"];

    private static string L(string key, string fallback) => LocalizationService.Get(key, fallback);

    private static string Lf(string key, string fallback, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, LocalizationService.Get(key, fallback), args);

    protected override void OnClosed(EventArgs e)
    {
        _analysisCancellation?.Cancel();
        _platformService.Dispose();
        base.OnClosed(e);
    }

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;

        if (AppIcon.Load() is { } icon)
        {
            Icon = icon;
            AppTitleBar.Icon = new ImageIcon
            {
                Source = icon,
                Width = 18,
                Height = 18
            };
        }
        AppIcon.EnsureFileBesideAssembly();

        BuildPages();
        ((DeepAnalysisView)_pages["deep"]).ConfigureRequested += (_, _) => NavigateTo("analyze");
        ((InsightsView)_pages["insights"]).OpenReportsRequested += (_, _) => NavigateTo("reports");

        AnalyzeViewControl.SelectFileRequested += (_, _) => SelectFile();
        AnalyzeViewControl.SelectRulesRequested += (_, _) => SelectRules();
        AnalyzeViewControl.ConfigureRulesRequested += (_, _) => ConfigureRules();
        AnalyzeViewControl.AnalyzeRequested += async (_, _) => await RunAnalysisAsync();
        AnalyzeViewControl.CancelRequested += (_, _) => CancelAnalysis();
        AnalyzeViewControl.OpenReportRequested += (_, _) => OpenReport();
        AnalyzeViewControl.OpenReportFolderRequested += (_, _) => OpenReportFolder();
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

        InputBindings.Add(new KeyBinding(new WindowRelayCommand(SelectFile), new KeyGesture(Key.O, ModifierKeys.Control)));
        InputBindings.Add(new KeyBinding(new WindowRelayCommand(() => _ = RunAnalysisAsync()), new KeyGesture(Key.F5)));
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
    }

    private FrameworkElement CreatePage(FrameworkElement view)
    {
        view.DataContext = _viewModel;
        view.HorizontalAlignment = HorizontalAlignment.Stretch;
        view.VerticalAlignment = VerticalAlignment.Stretch;
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
            _viewModel.SetStatus(Lf("Str.Status.HistoryInitFailed", "本地历史初始化失败：{0}", ex.Message));
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

        page.HorizontalAlignment = HorizontalAlignment.Stretch;
        page.VerticalAlignment = VerticalAlignment.Stretch;
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

    private void MenuOpenFile_Click(object sender, RoutedEventArgs e) => SelectFile();

    private void MenuSelectRules_Click(object sender, RoutedEventArgs e) => SelectRules();

    private async void MenuAnalyze_Click(object sender, RoutedEventArgs e) => await RunAnalysisAsync();

    private void MenuCancel_Click(object sender, RoutedEventArgs e) => CancelAnalysis();

    private void MenuOpenReport_Click(object sender, RoutedEventArgs e) => OpenReport();

    private void MenuOpenReportFolder_Click(object sender, RoutedEventArgs e) => OpenReportFolder();

    private void MenuExit_Click(object sender, RoutedEventArgs e) => Close();

    private void MenuNavigate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.MenuItem { Tag: string tag })
            NavigateTo(tag);
    }

    private void SetChromeBusy(bool busy)
    {
        AnalyzeViewControl.SetBusy(busy);
        ToolAnalyzeButton.IsEnabled = !busy;
        ToolCancelButton.IsEnabled = busy;
        MenuAnalyzeItem.IsEnabled = !busy;
        MenuCancelItem.IsEnabled = busy;
        ToolOpenButton.IsEnabled = !busy;
    }

    private sealed class WindowRelayCommand : ICommand
    {
        private readonly Action _execute;

        public WindowRelayCommand(Action execute) => _execute = execute;

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => _execute();

        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }
    }
}
