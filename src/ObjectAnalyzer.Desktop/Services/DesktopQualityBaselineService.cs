using System.Text.Json;
using ObjectAnalyzer.Platform;

namespace ObjectAnalyzer.Desktop.Services;

public sealed class DesktopQualityBaselineService
{
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ObjectAnalyzer",
        "quality-baseline.json");

    public QualityBaseline Load(string sourceName)
    {
        try
        {
            if (File.Exists(_path))
            {
                var all = JsonSerializer.Deserialize<Dictionary<string, QualityBaseline>>(File.ReadAllText(_path));
                if (all is not null && all.TryGetValue(sourceName, out var baseline))
                    return baseline;
            }
        }
        catch (JsonException)
        {
        }

        return new QualityBaseline(sourceName);
    }

    public void Save(QualityBaseline baseline)
    {
        baseline.Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

        Dictionary<string, QualityBaseline> all;
        try
        {
            all = File.Exists(_path)
                ? JsonSerializer.Deserialize<Dictionary<string, QualityBaseline>>(File.ReadAllText(_path))
                    ?? new Dictionary<string, QualityBaseline>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, QualityBaseline>(StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            all = new Dictionary<string, QualityBaseline>(StringComparer.OrdinalIgnoreCase);
        }

        all[baseline.SourceName] = baseline;
        File.WriteAllText(_path, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static QualityBaselineEvaluation Evaluate(QualityBaseline baseline, QualityMetrics metrics)
    {
        var failures = new List<QualityBaselineFailure>();
        Check(failures, "质量总分", metrics.OverallScore, baseline.MinimumOverallScore);
        Check(failures, "完整性", metrics.Completeness, baseline.MinimumCompleteness);
        Check(failures, "有效性", metrics.Validity, baseline.MinimumValidity);
        Check(failures, "唯一性", metrics.Uniqueness, baseline.MinimumUniqueness);
        Check(failures, "一致性", metrics.Consistency, baseline.MinimumConsistency);
        Check(failures, "异常控制", metrics.AnomalyControl, baseline.MinimumAnomalyControl);
        return new QualityBaselineEvaluation(baseline.SourceName, failures.Count == 0, metrics, failures);
    }

    private static void Check(List<QualityBaselineFailure> failures, string metric, double actual, double? minimum)
    {
        if (minimum.HasValue && actual < minimum.Value)
            failures.Add(new QualityBaselineFailure(metric, actual, minimum.Value));
    }
}
