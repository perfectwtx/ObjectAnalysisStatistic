using System.Windows;
using ObjectAnalyzer.Desktop.ViewModels;

namespace ObjectAnalyzer.Desktop.Views;

public partial class FieldValueDetailWindow : Window
{
    public FieldValueDetailWindow(DesktopFieldRow field)
    {
        InitializeComponent();

        Title = $"字段取值 · {field.FieldName}";
        TitleText.Text = field.FieldName;

        var parts = new List<string>
        {
            $"类型：{field.PrimaryType}",
            $"覆盖率：{field.CoverageText}",
            $"非空：{field.Count:N0}",
            $"空值：{field.NullCount:N0}"
        };

        if (field.DistinctCount is long distinct)
            parts.Add(field.DistinctApproximate ? $"唯一值：≈{distinct:N0}" : $"唯一值：{distinct:N0}");

        if (!string.IsNullOrWhiteSpace(field.SemanticType))
            parts.Add($"语义：{field.SemanticType}");

        if (!string.IsNullOrWhiteSpace(field.NumericRangeText))
            parts.Add($"数值范围：{field.NumericRangeText}");

        if (!string.IsNullOrWhiteSpace(field.QuantilesText))
            parts.Add(field.QuantilesText!);

        if (field.Types.Count > 0)
            parts.Add("类型分布：" + string.Join(", ", field.Types.Select(t => $"{t.TypeName}={t.Count}")));

        MetaText.Text = string.Join("  ·  ", parts);

        if (field.ValueCountsTruncated)
        {
            TruncationText.Visibility = Visibility.Visible;
            TruncationText.Text =
                "提示：唯一值超过分析阈值，以下为已采样/保留的取值分布（可能含 [Other values] 汇总桶），并非全部原始值。";
        }

        ValuesGrid.ItemsSource = field.Values;

        if (field.Values.Count == 0)
        {
            TruncationText.Visibility = Visibility.Visible;
            TruncationText.Text = string.IsNullOrWhiteSpace(TruncationText.Text)
                ? "当前字段没有可用的取值统计（ValueCounts 为空）。"
                : TruncationText.Text + Environment.NewLine + "当前字段没有可用的取值统计（ValueCounts 为空）。";
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
