using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ObjectAnalyzer.Analysis;

namespace ObjectAnalyzer.Desktop.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private string _inputPath = string.Empty;
    private string _status = "等待选择文件";
    private string _reportPath = "分析完成后自动生成 JSON 报告";
    private int _progress;
    private bool _isBusy;
    private AnalysisResult? _result;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string InputPath { get => _inputPath; set => Set(ref _inputPath, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string ReportPath { get => _reportPath; private set => Set(ref _reportPath, value); }
    public int Progress { get => _progress; private set => Set(ref _progress, value); }
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }
    public AnalysisResult? Result => _result;

    public ObservableCollection<DesktopFieldRow> Fields { get; } = new();
    public ObservableCollection<DesktopDuplicateRow> Duplicates { get; } = new();
    public ObservableCollection<DesktopIssueRow> Issues { get; } = new();
    public ObservableCollection<DesktopReportRow> Reports { get; } = new();

    public void SetRunning(string reportPath)
    {
        IsBusy = true;
        Progress = 0;
        Status = "正在分析...";
        ReportPath = reportPath;
    }

    public void SetCompleted(AnalysisResult result)
    {
        _result = result;
        IsBusy = false;
        Progress = 100;
        Status = "分析完成";
        RefreshCollections();
    }

    public void SetFailed(string message)
    {
        IsBusy = false;
        Status = $"分析失败：{message}";
    }

    public void SetProgress(long value) => Progress = Math.Clamp((int)value, 0, 100);

    public void Load(AnalysisResult result, string reportPath)
    {
        _result = result;
        ReportPath = reportPath;
        Status = "已加载历史报告";
        RefreshCollections();
    }

    public void RefreshCollections()
    {
        Fields.Clear();
        foreach (var field in (_result?.FieldStatistics ?? []).OrderBy(x => x.FieldName, StringComparer.OrdinalIgnoreCase))
            Fields.Add(new DesktopFieldRow(field));

        Duplicates.Clear();
        foreach (var duplicate in _result?.DuplicateObjects ?? [])
            Duplicates.Add(new DesktopDuplicateRow(duplicate));

        Issues.Clear();
        foreach (var issue in _result?.QualityViolations ?? [])
            Issues.Add(new DesktopIssueRow(issue));
    }

    public void ClearReports()
    {
        Reports.Clear();
    }

    public void AddReport(string path) => Reports.Add(new DesktopReportRow(path));

    public string GetDeepAnalysis(string name, object? value) =>
        value is null
            ? $"{name}\r\n\r\n未生成。请在分析时启用对应的深度分析选项。"
            : System.Text.Json.JsonSerializer.Serialize(value, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

    public string GetInsight(string title, object? value) =>
        value is null
            ? $"{title}\r\n\r\n当前报告没有生成该结果。"
            : System.Text.Json.JsonSerializer.Serialize(value, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class DesktopFieldRow
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

public sealed class DesktopDuplicateRow
{
    public string Key { get; }
    public long Count { get; }

    public DesktopDuplicateRow(DuplicateObjectStatistic source)
    {
        Key = source.ObjectJson.Length > 120 ? source.ObjectJson[..120] + "..." : source.ObjectJson;
        Count = source.Count;
    }
}

public sealed class DesktopIssueRow
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

public sealed class DesktopReportRow
{
    public string Path { get; }
    public string Name { get; }
    public string TimeText { get; }
    public string SizeText { get; }

    public DesktopReportRow(string path)
    {
        Path = path;
        var file = new FileInfo(path);
        Name = file.Name;
        TimeText = file.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss");
        SizeText = file.Length < 1024 * 1024
            ? $"{file.Length / 1024d:N1} KB"
            : $"{file.Length / 1024d / 1024d:N1} MB";
    }
}
