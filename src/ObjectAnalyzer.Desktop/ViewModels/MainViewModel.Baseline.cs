using System.Globalization;
using ObjectAnalyzer.Platform;

namespace ObjectAnalyzer.Desktop.ViewModels;

public sealed partial class MainViewModel
{
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

    private static string FormatThreshold(double? value) =>
        value?.ToString("0.##", CultureInfo.InvariantCulture) ?? "90";

    private static bool TryParse(string text, out double value) =>
        double.TryParse(
            text,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out value)
        && value is >= 0 and <= 100;
}
