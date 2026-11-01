using System.IO;
using System.Text.Json;

namespace ObjectAnalyzer.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

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

    public static string GetDeepAnalysis(string name, object? value) =>
        value is null
            ? $"{name}\r\n\r\n未生成。请在分析页勾选对应的深度分析选项后重新分析。"
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

    private static string Serialize(object value) =>
        JsonSerializer.Serialize(value, s_jsonOptions);
}
