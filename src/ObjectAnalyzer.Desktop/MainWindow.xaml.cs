using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using ObjectAnalyzer.Abstractions;
using ObjectAnalyzer.Analysis;

namespace ObjectAnalyzer.Desktop;

public partial class MainWindow : Window
{
    private string? _reportPath;
    private AnalysisResult? _result;

    public MainWindow()
    {
        InitializeComponent();
        RefreshReportHistory();
    }

    private void SelectFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择要分析的数据文件",
            Filter = "支持的文件|*.json;*.jsonl;*.csv;*.xlsx;*.xls;*.xml;*.yaml;*.yml;*.gz;*.bz2;*.zip|所有文件|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) == true)
        {
            InputPathTextBox.Text = dialog.FileName;
            StatusTextBlock.Text = "已选择文件，等待开始分析";
        }
    }

    private async void Analyze_Click(object sender, RoutedEventArgs e)
    {
        string inputPath = InputPathTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
        {
            MessageBox.Show(this, "请选择一个存在的数据文件。", "无法开始分析", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SetBusy(true);
        OutputTextBox.Clear();
        AnalysisProgress.Value = 0;
        StatusTextBlock.Text = "正在分析...";
        _reportPath = BuildReportPath(inputPath);
        ReportPathTextBlock.Text = _reportPath;

        var console = new DesktopConsole(message => Dispatcher.Invoke(() =>
        {
            OutputTextBox.AppendText(message + Environment.NewLine);
            OutputTextBox.ScrollToEnd();
        }));

        var progress = new DesktopProgressReporter(value => Dispatcher.Invoke(() =>
            AnalysisProgress.Value = Math.Clamp(value, 0, 100)));

        AnalysisServices.Use(new AnalysisServices
        {
            Console = console,
            Progress = progress,
            Presenter = NullResultPresenter.Instance
        });

        try
        {
            var options = new AnalysisOptions
            {
                Input = new ObjectAnalyzer.Analysis.Options.InputSourceOptions { InputPath = inputPath },
                Report = new ObjectAnalyzer.Analysis.Options.ReportOutputOptions { OutputPath = _reportPath },
                Progress = new ObjectAnalyzer.Analysis.Options.ProgressOptions { ShowProgress = true }
            };

            int exitCode = await Task.Run(() => AnalysisRunner.Run(options));
            StatusTextBlock.Text = exitCode == 0 ? "分析完成" : $"分析结束，退出码 {exitCode}";

            if (exitCode == 0 && File.Exists(_reportPath))
                LoadResult(_reportPath);

            AnalysisProgress.Value = 100;
            OpenReportButton.IsEnabled = exitCode == 0 && File.Exists(_reportPath);
            ExportCsvButton.IsEnabled = _result is not null;
            RefreshReportHistory();
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = "分析失败";
            OutputTextBox.AppendText($"错误: {ex.Message}{Environment.NewLine}");
        }
        finally
        {
            AnalysisServices.Use(null);
            SetBusy(false);
        }
    }

    private void LoadResult(string reportPath)
    {
        try
        {
            var json = File.ReadAllText(reportPath);
            _result = JsonSerializer.Deserialize<AnalysisResult>(json);
            if (_result is null)
                return;

            _reportPath = reportPath;
            ReportPathTextBlock.Text = reportPath;
            TotalObjectsText.Text = _result.TotalObjects.ToString("N0");
            FieldCountText.Text = _result.FieldStatistics.Count.ToString("N0");
            DuplicateGroupsText.Text = _result.TotalDuplicateGroups.ToString("N0");
            QualityIssuesText.Text = _result.QualityViolations.Count.ToString("N0");

            var fields = _result.FieldStatistics
                .OrderBy(x => x.FieldName, StringComparer.OrdinalIgnoreCase)
                .Select(x => new DesktopFieldRow(x))
                .ToList();
            FieldGrid.ItemsSource = fields;
            SchemaGrid.ItemsSource = fields;

            DuplicateSummaryText.Text = _result.DuplicateObjects.Count == 0
                ? $"未发现可展示的重复对象。总重复组：{_result.TotalDuplicateGroups:N0}"
                : $"共发现 {_result.TotalDuplicateGroups:N0} 个重复组，当前报告保存 {_result.DuplicateObjects.Count:N0} 组样本。";
            DuplicateGrid.ItemsSource = _result.DuplicateObjects.Select(x => new DesktopDuplicateRow(x)).ToList();

            IssueList.ItemsSource = _result.QualityViolations.Select(x => new DesktopIssueRow(x)).ToList();

            DeepAnalysisTextBox.Text = "请选择上方分析维度。\r\n\r\n未启用的深度分析会明确显示“未生成”，不会伪造分析结果。";
            InsightTextBox.Text = "请选择上方分析维度。\r\n\r\n本地模式基于当前 AnalysisResult 展示血缘、Schema 影响、分布漂移与质量回归信息。";
            ExportCsvButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            OutputTextBox.AppendText($"报告读取失败: {ex.Message}{Environment.NewLine}");
        }
    }

    private void ShowCorrelation_Click(object sender, RoutedEventArgs e) => ShowDeep(nameof(AnalysisResult.CorrelationMatrix), _result?.CorrelationMatrix);
    private void ShowFuzzy_Click(object sender, RoutedEventArgs e) => ShowDeep(nameof(AnalysisResult.FuzzyDuplicates), _result?.FuzzyDuplicates);
    private void ShowPatterns_Click(object sender, RoutedEventArgs e) => ShowDeep(nameof(AnalysisResult.StringPatterns), _result?.StringPatterns);
    private void ShowUnicode_Click(object sender, RoutedEventArgs e) => ShowDeep(nameof(AnalysisResult.UnicodeHygiene), _result?.UnicodeHygiene);
    private void ShowTimeSeries_Click(object sender, RoutedEventArgs e) => ShowDeep(nameof(AnalysisResult.TimeSeriesAnalyses), _result?.TimeSeriesAnalyses);
    private void ShowDistribution_Click(object sender, RoutedEventArgs e) => ShowDeep(nameof(AnalysisResult.DistributionDiff), _result?.DistributionDiff);
    private void ShowSampling_Click(object sender, RoutedEventArgs e) => ShowDeep(nameof(AnalysisResult.SamplingConfidence), _result?.SamplingConfidence);

    private void ShowDeep(string name, object? value)
    {
        DeepAnalysisTextBox.Text = value is null
            ? $"{name}\r\n\r\n未生成。请在分析时启用对应的深度分析选项。"
            : JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });
    }

    private void ShowLineage_Click(object sender, RoutedEventArgs e)
    {
        if (_result?.ObjectRelationships is not { Count: > 0 } relationships)
        {
            InsightTextBox.Text = "数据血缘\r\n\r\n当前结果未生成对象关系，因此没有可展示的关系血缘。";
            return;
        }

        var lines = relationships.Select(x =>
            $"{x.Kind,-18} {x.Path}  出现 {x.OccurrenceCount:N0} 次，非空 {x.NonNullCount:N0}，目标字段 {string.Join(", ", x.TargetFields)}");
        InsightTextBox.Text = string.Join(Environment.NewLine, lines);
    }

    private void ShowImpact_Click(object sender, RoutedEventArgs e)
    {
        if (_result?.ObjectRelationships is not { Count: > 0 } relationships)
        {
            InsightTextBox.Text = "影响分析\r\n\r\n当前结果未生成对象关系。影响分析需要关系数据作为输入。";
            return;
        }

        InsightTextBox.Text = "影响分析（基于当前本地结果的关系摘要）\r\n\r\n" +
            string.Join(Environment.NewLine, relationships.Select(x =>
                $"• {x.Path} → {string.Join(", ", x.TargetFields)} | {x.Kind} | {x.OccurrenceCount:N0} 次"));
    }

    private void ShowSchemaImpact_Click(object sender, RoutedEventArgs e) =>
        ShowInsightObject("Schema Impact", _result?.SchemaDiff);

    private void ShowRegression_Click(object sender, RoutedEventArgs e)
    {
        if (_result?.DistributionDiff is null)
        {
            InsightTextBox.Text = "质量回归\r\n\r\n当前报告没有 DistributionDiff。请使用基线 / 分布漂移分析后重新生成报告。";
            return;
        }

        InsightTextBox.Text = "质量回归 / 分布漂移\r\n\r\n" +
            JsonSerializer.Serialize(_result.DistributionDiff, new JsonSerializerOptions { WriteIndented = true });
    }

    private void ShowInsightObject(string title, object? value)
    {
        InsightTextBox.Text = value is null
            ? $"{title}\r\n\r\n当前报告没有生成该结果。"
            : JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });
    }

    private void OpenReport_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_reportPath) || !File.Exists(_reportPath))
            return;

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{_reportPath}\"",
            UseShellExecute = true
        });
    }

    private void RefreshReports_Click(object sender, RoutedEventArgs e) => RefreshReportHistory();

    private void RefreshReportHistory()
    {
        string directory = GetReportDirectory();
        if (!Directory.Exists(directory))
        {
            ReportHistoryList.ItemsSource = Array.Empty<DesktopReportRow>();
            return;
        }

        ReportHistoryList.ItemsSource = Directory.EnumerateFiles(directory, "*.json")
            .Select(path => new FileInfo(path))
            .OrderByDescending(x => x.LastWriteTime)
            .Take(100)
            .Select(x => new DesktopReportRow(x.FullName))
            .ToList();
    }

    private void ReportHistory_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ReportHistoryList.SelectedItem is DesktopReportRow row && File.Exists(row.Path))
            LoadResult(row.Path);
    }

    private void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        if (_result is null)
            return;

        var dialog = new SaveFileDialog
        {
            Title = "导出字段 CSV",
            Filter = "CSV 文件|*.csv",
            FileName = "object-analyzer-fields.csv"
        };
        if (dialog.ShowDialog(this) != true)
            return;

        var sb = new StringBuilder();
        sb.AppendLine("FieldName,PrimaryType,Coverage,Count,NullCount,DistinctCount");
        foreach (var field in _result.FieldStatistics.OrderBy(x => x.FieldName, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append(EscapeCsv(field.FieldName)).Append(',')
              .Append(EscapeCsv(field.PrimaryType)).Append(',')
              .Append(field.Coverage.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',')
              .Append(field.Count).Append(',')
              .Append(field.NullCount).Append(',')
              .Append(field.DistinctCount?.ToString() ?? string.Empty).AppendLine();
        }

        File.WriteAllText(dialog.FileName, sb.ToString(), new UTF8Encoding(true));
        StatusTextBlock.Text = $"CSV 已导出：{dialog.FileName}";
    }

    private static string EscapeCsv(string value)
    {
        if (!value.Contains(',') && !value.Contains('"') && !value.Contains('\n') && !value.Contains('\r'))
            return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static string BuildReportPath(string inputPath)
    {
        string directory = GetReportDirectory();
        Directory.CreateDirectory(directory);
        string name = Path.GetFileNameWithoutExtension(inputPath);
        return Path.Combine(directory, $"{name}-{DateTime.Now:yyyyMMdd-HHmmss}.json");
    }

    private static string GetReportDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ObjectAnalyzer", "Reports");

    private void SetBusy(bool busy)
    {
        AnalyzeButton.IsEnabled = !busy;
        InputPathTextBox.IsEnabled = !busy;
        FooterTextBlock.Text = busy
            ? "Object Analyzer Desktop · 正在执行本地分析"
            : "Object Analyzer Desktop · .NET 10 · WPF";
    }
}

internal sealed class DesktopConsole : IAnalysisConsole
{
    private readonly Action<string> _write;
    public DesktopConsole(Action<string> write) => _write = write;
    public void MarkupLine(string markup) => _write(StripMarkup(markup));
    public void WriteLine(string text = "") => _write(text);
    public void Write(string text) => _write(text);
    private static string StripMarkup(string value) =>
        System.Text.RegularExpressions.Regex.Replace(value, @"\[/?[a-zA-Z0-9 #;,.:-]+\]", string.Empty);
}

internal sealed class DesktopProgressReporter : IProgressReporter
{
    private readonly Action<long> _report;
    private long _lastValue;
    public DesktopProgressReporter(Action<long> report) => _report = report;

    public void Run(string description, long? maxObjects, bool showProgress, Action<IProgress<long>?> work)
    {
        var progress = new Progress<long>(value =>
        {
            _lastValue = value;
            if (maxObjects is > 0)
                _report((long)((double)value / maxObjects.Value * 100));
        });
        work(showProgress ? progress : null);
        if (maxObjects is null || maxObjects <= 0)
            _report(_lastValue > 0 ? 100 : 0);
    }
}

internal sealed class DesktopFieldRow
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

internal sealed class DesktopDuplicateRow
{
    public string Key { get; }
    public long Count { get; }
    public DesktopDuplicateRow(DuplicateObjectStatistic source)
    {
        Key = source.ObjectJson.Length > 120 ? source.ObjectJson[..120] + "..." : source.ObjectJson;
        Count = source.Count;
    }
}

internal sealed class DesktopIssueRow
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

internal sealed class DesktopReportRow
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
