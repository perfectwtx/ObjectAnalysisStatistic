using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ObjectAnalyzer.Analysis;
using ObjectAnalyzer.Platform;

namespace ObjectAnalyzer.Desktop.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private string _inputPath = string.Empty;
    private string _status = "等待选择文件";
    private string _reportPath = "分析完成后自动生成 JSON 报告";
    private int _progress;
    private bool _isBusy;
    private AnalysisResult? _result;
    private string _outputText = string.Empty;
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    private string _baselineSourceName = string.Empty;
    private string _minimumOverallScore = "90";
    private string _minimumCompleteness = "90";
    private string _minimumValidity = "90";
    private string _minimumUniqueness = "90";
    private string _minimumConsistency = "90";
    private string _minimumAnomalyControl = "90";

    public event PropertyChangedEventHandler? PropertyChanged;

    public string InputPath { get => _inputPath; set => Set(ref _inputPath, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string ReportPath { get => _reportPath; private set => Set(ref _reportPath, value); }
    public int Progress { get => _progress; private set => Set(ref _progress, value); }
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }
    public AnalysisResult? Result => _result;
    public string OutputText { get => _outputText; private set => Set(ref _outputText, value); }

    public long TotalObjects => _result?.TotalObjects ?? 0;
    public int FieldCount => _result?.FieldStatistics.Count ?? 0;
    public long DuplicateGroups => _result?.TotalDuplicateGroups ?? 0;
    public int QualityIssueCount => _result?.QualityViolations.Count ?? 0;
    public string DuplicateSummary => _result is null
        ? "暂无重复对象结果。"
        : $"重复对象组：{_result.TotalDuplicateGroups:N0}，唯一对象：{_result.TotalUniqueObjects:N0}";
    public bool HasResult => _result is not null;
    public bool HasReport => File.Exists(_reportPath) && !string.Equals(
        _reportPath, "分析完成后自动生成 JSON 报告", StringComparison.Ordinal);

    public bool HasFields => Fields.Count > 0;
    public bool HasDuplicates => Duplicates.Count > 0;
    public bool HasIssues => Issues.Count > 0;
    public bool HasJobs => Jobs.Count > 0;
    public bool HasReports => Reports.Count > 0;
    public bool HasQualityTrend => QualityTrend.Count > 0;
    public bool HasOutput => !string.IsNullOrWhiteSpace(OutputText);

    public string BaselineSourceName { get => _baselineSourceName; set => Set(ref _baselineSourceName, value); }
    public string MinimumOverallScore { get => _minimumOverallScore; set => Set(ref _minimumOverallScore, value); }
    public string MinimumCompleteness { get => _minimumCompleteness; set => Set(ref _minimumCompleteness, value); }
    public string MinimumValidity { get => _minimumValidity; set => Set(ref _minimumValidity, value); }
    public string MinimumUniqueness { get => _minimumUniqueness; set => Set(ref _minimumUniqueness, value); }
    public string MinimumConsistency { get => _minimumConsistency; set => Set(ref _minimumConsistency, value); }
    public string MinimumAnomalyControl { get => _minimumAnomalyControl; set => Set(ref _minimumAnomalyControl, value); }

    public ObservableCollection<DesktopFieldRow> Fields { get; } = new();
    public ObservableCollection<DesktopDuplicateRow> Duplicates { get; } = new();
    public ObservableCollection<DesktopIssueRow> Issues { get; } = new();
    public ObservableCollection<DesktopReportRow> Reports { get; } = new();
    public ObservableCollection<DesktopQualitySnapshotRow> QualityTrend { get; } = new();
    public ObservableCollection<DesktopJobRow> Jobs { get; } = new();
    public ObservableCollection<string> RecentFiles { get; } = new();

    public void SetRunning(string reportPath)
    {
        IsBusy = true;
        Progress = 0;
        ReportPath = reportPath;
        OnPropertyChanged(nameof(HasReport));
        SetStatus("正在分析...");
    }

    public void SetCompleted(AnalysisResult result)
    {
        _result = result;
        IsBusy = false;
        Progress = 100;
        SetStatus("分析完成");
        RefreshCollections();
        RaiseResultStateChanged();
    }

    public void SetFailed(string message)
    {
        IsBusy = false;
        SetStatus($"分析失败：{message}");
    }

    public void SetProgress(long value) => Progress = Math.Clamp((int)value, 0, 100);

    public void Load(AnalysisResult result, string reportPath)
    {
        _result = result;
        ReportPath = reportPath;
        RefreshCollections();
        RaiseResultStateChanged();
    }

    public void ClearOutput()
    {
        OutputText = string.Empty;
        OnPropertyChanged(nameof(HasOutput));
    }

    public void AppendOutput(string message)
    {
        OutputText = string.IsNullOrEmpty(OutputText)
            ? message
            : OutputText + Environment.NewLine + message;
        OnPropertyChanged(nameof(HasOutput));
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

        RaiseCollectionStateChanged();
    }

    public void SetQualityTrend(IEnumerable<QualitySnapshot> snapshots)
    {
        QualityTrend.Clear();
        foreach (var snapshot in snapshots.OrderBy(x => x.OccurredAtUtc))
            QualityTrend.Add(new DesktopQualitySnapshotRow(snapshot));
        OnPropertyChanged(nameof(HasQualityTrend));
    }

    public void SetJobs(IEnumerable<AnalysisJob> jobs)
    {
        Jobs.Clear();
        foreach (var job in jobs.OrderByDescending(x => x.StartedAtUtc))
            Jobs.Add(new DesktopJobRow(job));
        OnPropertyChanged(nameof(HasJobs));
    }

    public void ClearReports()
    {
        Reports.Clear();
        OnPropertyChanged(nameof(HasReports));
    }

    public void AddReport(string path)
    {
        Reports.Add(new DesktopReportRow(path));
        OnPropertyChanged(nameof(HasReports));
    }

    public void SetBaseline(QualityBaseline? baseline, string sourceName)
    {
        BaselineSourceName = sourceName;
        MinimumOverallScore = FormatThreshold(baseline?.MinimumOverallScore);
        MinimumCompleteness = FormatThreshold(baseline?.MinimumCompleteness);
        MinimumValidity = FormatThreshold(baseline?.MinimumValidity);
        MinimumUniqueness = FormatThreshold(baseline?.MinimumUniqueness);
        MinimumConsistency = FormatThreshold(baseline?.MinimumConsistency);
        MinimumAnomalyControl = FormatThreshold(baseline?.MinimumAnomalyControl);
    }

    public bool TryBuildBaseline(out QualityBaseline? baseline, out string error)
    {
        baseline = null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(BaselineSourceName))
        {
            error = "数据源名称不能为空。";
            return false;
        }

        if (!TryParse(MinimumOverallScore, out var overall)
            || !TryParse(MinimumCompleteness, out var completeness)
            || !TryParse(MinimumValidity, out var validity)
            || !TryParse(MinimumUniqueness, out var uniqueness)
            || !TryParse(MinimumConsistency, out var consistency)
            || !TryParse(MinimumAnomalyControl, out var anomalyControl))
        {
            error = "质量阈值必须是 0 到 100 之间的数字。";
            return false;
        }

        baseline = new QualityBaseline(
            BaselineSourceName.Trim(),
            overall,
            completeness,
            validity,
            uniqueness,
            consistency,
            anomalyControl);

        try
        {
            baseline.Validate();
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            baseline = null;
            return false;
        }
    }

    public static string GetDeepAnalysis(string name, object? value) =>
        value is null
            ? $"{name}\r\n\r\n未生成。请在分析时启用对应的深度分析选项。"
            : Serialize(value);

    public static string GetInsight(string title, object? value) =>
        value is null
            ? $"{title}\r\n\r\n当前报告没有生成该结果。"
            : Serialize(value);

    public static string BuildReportDetails(DesktopReportRow? report)
    {
        if (report is null)
            return "请选择报告。";

        return $"报告：{report.Name}\r\n路径：{report.Path}\r\n修改时间：{report.TimeText}\r\n大小：{report.SizeText}";
    }

    public void SetStatus(string value) => Status = value;

    public void SetRecentFiles(IEnumerable<string> paths)
    {
        RecentFiles.Clear();
        foreach (var path in paths.Take(10))
            RecentFiles.Add(path);
    }

    public bool AddRecentFile(string path)
    {
        if (!File.Exists(path))
            return false;

        string normalized = Path.GetFullPath(path);
        for (int index = RecentFiles.Count - 1; index >= 0; index--)
        {
            if (string.Equals(RecentFiles[index], normalized, StringComparison.OrdinalIgnoreCase))
                RecentFiles.RemoveAt(index);
        }

        RecentFiles.Insert(0, normalized);
        while (RecentFiles.Count > 10)
            RecentFiles.RemoveAt(RecentFiles.Count - 1);

        return true;
    }

    private void RaiseResultStateChanged()
    {
        OnPropertyChanged(nameof(Result));
        OnPropertyChanged(nameof(TotalObjects));
        OnPropertyChanged(nameof(FieldCount));
        OnPropertyChanged(nameof(DuplicateGroups));
        OnPropertyChanged(nameof(QualityIssueCount));
        OnPropertyChanged(nameof(DuplicateSummary));
        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(HasReport));
        RaiseCollectionStateChanged();
    }

    private void RaiseCollectionStateChanged()
    {
        OnPropertyChanged(nameof(HasFields));
        OnPropertyChanged(nameof(HasDuplicates));
        OnPropertyChanged(nameof(HasIssues));
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;

        field = value;
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged(string? propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private static string FormatThreshold(double? value) =>
        value?.ToString("0.##", CultureInfo.InvariantCulture) ?? "90";

    private static bool TryParse(string text, out double value) =>
        double.TryParse(
            text,
            System.Globalization.NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out value)
        && value is >= 0 and <= 100;

    private static string Serialize(object value) =>
        JsonSerializer.Serialize(value, s_jsonOptions);
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
        CoverageText = source.Coverage.ToString("P1", CultureInfo.InvariantCulture);
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
        TimeText = file.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        SizeText = file.Length < 1024 * 1024
            ? $"{(file.Length / 1024d).ToString("N1", CultureInfo.InvariantCulture)} KB"
            : $"{(file.Length / 1024d / 1024d).ToString("N1", CultureInfo.InvariantCulture)} MB";
    }
}

public sealed class DesktopQualitySnapshotRow
{
    public string SourceName { get; }
    public string TimeText { get; }
    public string OverallText { get; }
    public string CompletenessText { get; }
    public string ValidityText { get; }
    public string UniquenessText { get; }
    public string ConsistencyText { get; }
    public string AnomalyControlText { get; }
    public long TotalObjects { get; }
    public int IssueCount { get; }

    public DesktopQualitySnapshotRow(QualitySnapshot snapshot)
    {
        SourceName = snapshot.SourceName;
        TimeText = snapshot.OccurredAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        OverallText = snapshot.Metrics.OverallScore.ToString("F2", CultureInfo.InvariantCulture);
        CompletenessText = snapshot.Metrics.Completeness.ToString("F2", CultureInfo.InvariantCulture);
        ValidityText = snapshot.Metrics.Validity.ToString("F2", CultureInfo.InvariantCulture);
        UniquenessText = snapshot.Metrics.Uniqueness.ToString("F2", CultureInfo.InvariantCulture);
        ConsistencyText = snapshot.Metrics.Consistency.ToString("F2", CultureInfo.InvariantCulture);
        AnomalyControlText = snapshot.Metrics.AnomalyControl.ToString("F2", CultureInfo.InvariantCulture);
        TotalObjects = snapshot.TotalObjects;
        IssueCount = snapshot.IssueCount;
    }
}

public sealed class DesktopJobRow
{
    public Guid Id { get; }
    public string SourceName { get; }
    public string SourceType { get; }
    public string StatusText { get; }
    public string StartedText { get; }
    public string DurationText { get; }
    public long TotalObjects { get; }
    public int FieldCount { get; }
    public string QualityScoreText { get; }
    public int IssueCount { get; }
    public string? ErrorMessage { get; }

    public DesktopJobRow(AnalysisJob job)
    {
        Id = job.Id;
        SourceName = job.SourceName;
        SourceType = job.SourceType;
        StatusText = job.Status switch
        {
            AnalysisJobStatus.Pending => "等待",
            AnalysisJobStatus.Running => "运行中",
            AnalysisJobStatus.Succeeded => "成功",
            AnalysisJobStatus.Failed => "失败",
            AnalysisJobStatus.Cancelled => "已取消",
            _ => job.Status.ToString()
        };
        StartedText = job.StartedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        DurationText = job.DurationMs > 0
            ? $"{job.DurationMs.ToString("N0", CultureInfo.InvariantCulture)} ms"
            : "-";
        TotalObjects = job.TotalObjects;
        FieldCount = job.FieldCount;
        QualityScoreText = job.QualityScore?.ToString("F2", CultureInfo.InvariantCulture) ?? "-";
        IssueCount = job.IssueCount;
        ErrorMessage = job.ErrorMessage;
    }
}
