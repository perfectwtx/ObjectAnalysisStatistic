using System.Text.Json;
using ObjectAnalyzer.Analysis;
using ObjectAnalyzer.Platform;

namespace ObjectAnalyzer.Desktop.Services;

public sealed class ReportHistoryService
{
    public IReadOnlyList<DesktopReportInfo> GetReports(int limit = 100)
    {
        var directory = GetReportDirectory();
        if (!Directory.Exists(directory))
            return [];

        return Directory.EnumerateFiles(directory, "*.json")
            .Select(path => TryRead(path))
            .Where(x => x is not null)
            .Cast<DesktopReportInfo>()
            .OrderByDescending(x => x.LastWriteTime)
            .Take(limit)
            .ToList();
    }

    public DesktopReportInfo? Load(string path)
    {
        if (!File.Exists(path))
            return null;
        return TryRead(path);
    }

    private static DesktopReportInfo? TryRead(string path)
    {
        try
        {
            var result = JsonSerializer.Deserialize<AnalysisResult>(File.ReadAllText(path));
            if (result is null)
                return null;

            var metrics = QualityMetricsCalculator.Calculate(result);
            var file = new FileInfo(path);
            return new DesktopReportInfo(
                path,
                file.Name,
                file.LastWriteTime,
                file.Length,
                result.TotalObjects,
                result.FieldStatistics.Count,
                result.QualityViolations.Count,
                result.TotalDuplicateGroups,
                metrics);
        }
        catch (Exception) when (true)
        {
            return null;
        }
    }

    public static string GetReportDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ObjectAnalyzer", "Reports");
}

public sealed record DesktopReportInfo(
    string Path,
    string Name,
    DateTime LastWriteTime,
    long Size,
    long TotalObjects,
    int FieldCount,
    int QualityIssueCount,
    long DuplicateGroups,
    QualityMetrics Metrics);
