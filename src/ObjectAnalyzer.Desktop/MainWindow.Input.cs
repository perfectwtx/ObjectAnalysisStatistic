using System.IO;
using System.Windows;
using Microsoft.Win32;
using ObjectAnalyzer.Desktop.Services;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace ObjectAnalyzer.Desktop;

public partial class MainWindow
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".json", ".jsonl", ".csv", ".xlsx", ".xls", ".xml", ".yaml", ".yml", ".gz", ".bz2", ".zip"
    };

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

    private void ConfigureRules()
    {
        var window = new RulesConfigWindow(_viewModel.RulesPath)
        {
            Owner = this
        };

        if (window.ShowDialog() == true && !string.IsNullOrWhiteSpace(window.SavedRulesPath))
        {
            _viewModel.RulesPath = window.SavedRulesPath;
            _viewModel.SetStatus($"规则配置已保存：{Path.GetFileName(window.SavedRulesPath)}");
        }
    }

    private void SelectRules()
    {
        var dialog = new OpenFileDialog
        {
            Title = L("Str.Dialog.SelectRulesTitle", "选择规则文件"),
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
            _viewModel.SetStatus(L("Str.Status.RecentFileInvalid", "最近文件已失效，请重新选择文件"));
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
}
