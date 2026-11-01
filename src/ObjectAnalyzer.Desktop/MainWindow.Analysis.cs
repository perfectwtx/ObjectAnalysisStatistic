using System.Diagnostics;
using System.IO;
using System.Windows;
using ObjectAnalyzer.Desktop.Services;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace ObjectAnalyzer.Desktop;

public partial class MainWindow
{
    private async Task RunAnalysisAsync()
    {
        if (_viewModel.IsBusy)
            return;

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
            MessageBox.Show(this,
                L("Str.Dialog.RulesFileMissing", "规则文件不存在，请重新选择。"),
                L("Str.Dialog.CannotStart", "无法开始分析"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string jsonReportPath = BuildReportPath(inputPath, "json");
        string userFormat = string.IsNullOrWhiteSpace(_viewModel.ReportFormat) ? "json" : _viewModel.ReportFormat.Trim();
        string userReportPath = string.Equals(userFormat, "json", StringComparison.OrdinalIgnoreCase)
            ? jsonReportPath
            : BuildReportPath(inputPath, userFormat);
        _viewModel.SetRunning(userReportPath);
        _viewModel.ClearOutput();
        SetChromeBusy(true);

        using var cancellation = new CancellationTokenSource();
        _analysisCancellation = cancellation;

        try
        {
            var settings = _viewModel.BuildAnalysisSettings();
            var result = await LocalAnalysisService.AnalyzeAsync(
                inputPath,
                jsonReportPath,
                userReportPath,
                rulesPath,
                settings,
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
                _viewModel.SetStatus(L("Str.Status.CompletedNonJson", "分析完成（非 JSON 报告，工作台字段统计可能为空）"));
                _viewModel.SetProgress(100);
            }
            else
            {
                _viewModel.SetFailed(Lf("Str.Status.ExitedWithCode", "分析结束，退出码 {0}", result.ExitCode));
            }
        }
        catch (OperationCanceledException)
        {
            _viewModel.SetFailed(L("Str.Status.Canceled", "分析已取消"));
        }
        catch (Exception ex)
        {
            _viewModel.SetFailed(ex.Message);
            _viewModel.AppendOutput($"{L("Str.Output.ErrorPrefix", "错误: ")}{ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_analysisCancellation, cancellation))
                _analysisCancellation = null;

            AnalyzeViewControl.SetBusy(false);
            SetChromeBusy(false);
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

        _viewModel.SetStatus(L("Str.Status.Canceling", "正在取消分析..."));
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

    private static void OpenReportFolder()
    {
        string directory = GetReportDirectory();
        Directory.CreateDirectory(directory);
        Process.Start(new ProcessStartInfo
        {
            FileName = directory,
            UseShellExecute = true
        });
    }

    private void LoadReport(string reportPath)
    {
        try
        {
            if (!reportPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this,
                    L("Str.Dialog.JsonReportOnly", "工作台仅支持回读 JSON 分析报告。"),
                    L("Str.Dialog.CannotOpenReport", "无法打开报告"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var result = LocalAnalysisService.LoadResult(reportPath);
            _viewModel.Load(result, reportPath);
            _viewModel.SetStatus(Lf("Str.Status.ReportLoaded", "已加载历史报告：{0}", Path.GetFileName(reportPath)));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                Lf("Str.Dialog.ReportLoadFailed", "报告读取失败：{0}", ex.Message),
                L("Str.Dialog.CannotOpenReport", "无法打开报告"),
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
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
