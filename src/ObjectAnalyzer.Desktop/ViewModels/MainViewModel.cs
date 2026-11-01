using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using ObjectAnalyzer.Analysis;
using ObjectAnalyzer.Desktop.Services;

namespace ObjectAnalyzer.Desktop.ViewModels;

public sealed partial class MainViewModel : INotifyPropertyChanged
{
    private string _inputPath = string.Empty;
    private string _rulesPath = string.Empty;
    private string _reportFormat = "json";
    private string _status = "等待选择文件";
    private string _reportPath = "分析完成后自动生成报告";
    private int _progress;
    private bool _isBusy;
    private AnalysisResult? _result;
    private string _outputText = string.Empty;

    private string _baselineSourceName = string.Empty;
    private string _minimumOverallScore = "90";
    private string _minimumCompleteness = "90";
    private string _minimumValidity = "90";
    private string _minimumUniqueness = "90";
    private string _minimumConsistency = "90";
    private string _minimumAnomalyControl = "90";

    private bool _enableCorrelation;
    private bool _enableStringPatterns;
    private bool _enableUnicodeHygiene;
    private bool _enableTimeSeries;
    private bool _enableDistributionSnapshot;
    private string _fuzzyDedupField = string.Empty;
    private string _sampleReservoirText = "0";
    private string _fieldFilter = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string InputPath
    {
        get => _inputPath;
        set
        {
            if (Set(ref _inputPath, value))
                OnPropertyChanged(nameof(DisplayFileName));
        }
    }
    public string RulesPath { get => _rulesPath; set => Set(ref _rulesPath, value); }
    public string ReportFormat { get => _reportFormat; set => Set(ref _reportFormat, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string DisplayFileName =>
        string.IsNullOrWhiteSpace(_inputPath) ? "—" : Path.GetFileName(_inputPath);
    public string ReportPath { get => _reportPath; private set => Set(ref _reportPath, value); }
    public int Progress { get => _progress; private set => Set(ref _progress, value); }
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }
    public AnalysisResult? Result => _result;
    public string OutputText { get => _outputText; private set => Set(ref _outputText, value); }

    public bool EnableCorrelation { get => _enableCorrelation; set => Set(ref _enableCorrelation, value); }
    public bool EnableStringPatterns { get => _enableStringPatterns; set => Set(ref _enableStringPatterns, value); }
    public bool EnableUnicodeHygiene { get => _enableUnicodeHygiene; set => Set(ref _enableUnicodeHygiene, value); }
    public bool EnableTimeSeries { get => _enableTimeSeries; set => Set(ref _enableTimeSeries, value); }
    public bool EnableDistributionSnapshot { get => _enableDistributionSnapshot; set => Set(ref _enableDistributionSnapshot, value); }
    public string FuzzyDedupField { get => _fuzzyDedupField; set => Set(ref _fuzzyDedupField, value); }
    public string SampleReservoirText { get => _sampleReservoirText; set => Set(ref _sampleReservoirText, value); }

    public string FieldFilter
    {
        get => _fieldFilter;
        set
        {
            if (Set(ref _fieldFilter, value))
                ApplyFieldFilter();
        }
    }

    public long TotalObjects => _result?.TotalObjects ?? 0;
    public int FieldCount => _result?.FieldStatistics.Count ?? 0;
    public long DuplicateGroups => _result?.TotalDuplicateGroups ?? 0;
    public int QualityIssueCount => _result?.QualityViolations.Count ?? 0;
    public string DuplicateSummary => _result is null
        ? "暂无重复对象结果。"
        : $"重复对象组：{_result.TotalDuplicateGroups:N0}，唯一对象：{_result.TotalUniqueObjects:N0}";
    public bool HasResult => _result is not null && _result.FieldStatistics.Count > 0;
    public bool HasReport => File.Exists(_reportPath);

    public bool HasFields => Fields.Count > 0;
    public string AverageCoverageText => HasFields
        ? Fields.Average(f => f.CoverageRatio).ToString("P1", CultureInfo.InvariantCulture) : "—";
    public int FullCoverageFields => Fields.Count(f => f.CoverageRatio >= 1);
    public int FieldsWithNulls => Fields.Count(f => f.NullCount > 0);
    public IEnumerable<DesktopFieldRow> LowestCoverageFields => Fields
        .Where(f => f.CoverageRatio < 1)
        .OrderBy(f => f.CoverageRatio).ThenBy(f => f.FieldName, StringComparer.OrdinalIgnoreCase).Take(5);
    public bool HasCoverageGaps => Fields.Any(f => f.CoverageRatio < 1);
    public IEnumerable<DesktopFieldTypeRow> FieldTypeSummary => Fields
        .GroupBy(f => f.PrimaryType).OrderByDescending(g => g.Count()).ThenBy(g => g.Key)
        .Select(g => new DesktopFieldTypeRow(g.Key, g.Count()));
    public bool HasFilteredFields => FilteredFields.Count > 0;
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
    public ObservableCollection<DesktopFieldRow> FilteredFields { get; } = new();
    public ObservableCollection<DesktopDuplicateRow> Duplicates { get; } = new();
    public ObservableCollection<DesktopIssueRow> Issues { get; } = new();
    public ObservableCollection<DesktopReportRow> Reports { get; } = new();
    public ObservableCollection<DesktopQualitySnapshotRow> QualityTrend { get; } = new();
    public ObservableCollection<DesktopJobRow> Jobs { get; } = new();
    public ObservableCollection<string> RecentFiles { get; } = new();

    public DesktopAnalysisSettings BuildAnalysisSettings()
    {
        int.TryParse(SampleReservoirText?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int reservoir);
        if (reservoir < 0) reservoir = 0;

        return new DesktopAnalysisSettings
        {
            EnableCorrelation = EnableCorrelation,
            EnableStringPatterns = EnableStringPatterns,
            EnableUnicodeHygiene = EnableUnicodeHygiene,
            EnableTimeSeries = EnableTimeSeries,
            EnableDistributionSnapshot = EnableDistributionSnapshot,
            FuzzyDedupField = FuzzyDedupField ?? string.Empty,
            SampleReservoir = reservoir
        };
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
        OnPropertyChanged(nameof(AverageCoverageText));
        OnPropertyChanged(nameof(FullCoverageFields));
        OnPropertyChanged(nameof(FieldsWithNulls));
        OnPropertyChanged(nameof(LowestCoverageFields));
        OnPropertyChanged(nameof(HasCoverageGaps));
        OnPropertyChanged(nameof(FieldTypeSummary));
        OnPropertyChanged(nameof(HasFields));
        OnPropertyChanged(nameof(HasFilteredFields));
        OnPropertyChanged(nameof(HasDuplicates));
        OnPropertyChanged(nameof(HasIssues));
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged(string? propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
