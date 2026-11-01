using ObjectAnalyzer.Analysis;
using ObjectAnalyzer.Platform;

namespace ObjectAnalyzer.Desktop.ViewModels;

public sealed partial class MainViewModel
{
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
        long totalObjects = _result?.TotalObjects ?? 0;
        foreach (var field in (_result?.FieldStatistics ?? []).OrderBy(x => x.FieldName, StringComparer.OrdinalIgnoreCase))
            Fields.Add(new DesktopFieldRow(field, totalObjects));

        ApplyFieldFilter();

        Duplicates.Clear();
        foreach (var duplicate in _result?.DuplicateObjects ?? [])
            Duplicates.Add(new DesktopDuplicateRow(duplicate));

        Issues.Clear();
        foreach (var issue in _result?.QualityViolations ?? [])
            Issues.Add(new DesktopIssueRow(issue));

        RaiseCollectionStateChanged();
    }

    public void ApplyFieldFilter()
    {
        FilteredFields.Clear();
        string filter = FieldFilter?.Trim() ?? string.Empty;
        IEnumerable<DesktopFieldRow> query = Fields;
        if (!string.IsNullOrEmpty(filter))
        {
            query = Fields.Where(f =>
                f.FieldName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || f.PrimaryType.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || (f.SemanticType?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        foreach (var row in query)
            FilteredFields.Add(row);

        OnPropertyChanged(nameof(HasFilteredFields));
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

    public void SetStatus(string value) => Status = value;
}
