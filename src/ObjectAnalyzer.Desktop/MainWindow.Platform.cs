using System.IO;
using System.Windows;
using ObjectAnalyzer.Desktop.Services;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace ObjectAnalyzer.Desktop;

public partial class MainWindow
{
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
            _viewModel.SetStatus(L("Str.Status.JobsRefreshed", "作业历史已刷新"));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                Lf("Str.Dialog.JobsLoadFailed", "作业历史读取失败：{0}", ex.Message),
                L("Str.Dialog.RefreshFailed", "刷新失败"),
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task LoadBaselineFromViewAsync()
    {
        try
        {
            await LoadBaselineAsync();
            _viewModel.SetStatus(L("Str.Status.BaselineLoaded", "质量基线已读取"));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                Lf("Str.Dialog.BaselineLoadFailed", "质量基线读取失败：{0}", ex.Message),
                L("Str.Dialog.LoadFailed", "读取失败"),
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task SaveBaselineFromViewAsync()
    {
        if (!_viewModel.TryBuildBaseline(out var baseline, out var error) || baseline is null)
        {
            MessageBox.Show(this, error,
                L("Str.Dialog.CannotSaveBaseline", "无法保存质量基线"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            await _platformService.InitializeAsync();
            await _platformService.BaselineStore.SaveAsync(baseline);
            _viewModel.SetStatus(Lf("Str.Status.BaselineSaved", "质量基线已保存：{0}", baseline.SourceName));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                Lf("Str.Dialog.BaselineSaveFailed", "质量基线保存失败：{0}", ex.Message),
                L("Str.Dialog.SaveFailed", "保存失败"),
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task DeleteBaselineFromViewAsync()
    {
        string sourceName = ResolveSourceName();
        if (string.IsNullOrWhiteSpace(sourceName))
        {
            MessageBox.Show(this,
                L("Str.Dialog.NeedSourceName", "请先指定数据源名称。"),
                L("Str.Dialog.CannotDelete", "无法删除"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            await _platformService.InitializeAsync();
            await _platformService.BaselineStore.DeleteAsync(sourceName);
            _viewModel.SetBaseline(null, sourceName);
            _viewModel.SetStatus(Lf("Str.Status.BaselineDeleted", "质量基线已删除：{0}", sourceName));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                Lf("Str.Dialog.BaselineDeleteFailed", "质量基线删除失败：{0}", ex.Message),
                L("Str.Dialog.DeleteFailed", "删除失败"),
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
}
