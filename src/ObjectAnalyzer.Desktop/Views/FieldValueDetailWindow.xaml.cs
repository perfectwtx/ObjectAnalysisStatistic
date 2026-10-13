using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Win32;
using ObjectAnalyzer.Desktop.ViewModels;

namespace ObjectAnalyzer.Desktop.Views;

public partial class FieldValueDetailWindow : Window
{
    private readonly DesktopFieldRow _field;

    public FieldValueDetailWindow(DesktopFieldRow field)
    {
        InitializeComponent();
        _field = field;

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
            parts.Add(field.DistinctApproximate ? $"唯一值：约 {distinct:N0}" : $"唯一值：{distinct:N0}");
        else
            parts.Add("唯一值：未知");

        if (!string.IsNullOrWhiteSpace(field.SemanticType))
            parts.Add($"语义：{field.SemanticType}");

        if (!string.IsNullOrWhiteSpace(field.NumericRangeText))
            parts.Add($"数值范围：{field.NumericRangeText}");

        if (!string.IsNullOrWhiteSpace(field.QuantilesText))
            parts.Add(field.QuantilesText!);

        if (field.Types.Count > 0)
            parts.Add("类型分布：" + string.Join(", ", field.Types.Select(t => $"{t.TypeName}={t.Count:N0}")));

        MetaText.Text = string.Join("  ·  ", parts);

        if (field.ValueCountsTruncated)
        {
            TruncationText.Visibility = Visibility.Visible;
            TruncationText.Text =
                "部分结果：取值数量超过分析阈值，表格仅展示已保留的取值（可能包含 [Other values] 汇总桶），不能视为完整明细。复制或导出也只包含当前保留的这些取值。";
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

    private void CopyAll_Click(object sender, RoutedEventArgs e)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Value\tCount\tShare");
        foreach (var row in _field.Values)
            sb.Append(row.Value).Append('\t').Append(row.Count).Append('\t').AppendLine(row.ShareText);

        Clipboard.SetText(sb.ToString());
        MessageBox.Show(this, $"已复制 {_field.Values.Count} 条取值到剪贴板。", "复制完成",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出字段取值",
            Filter = "CSV 文件|*.csv",
            FileName = $"{SanitizeFileName(_field.FieldName)}-values.csv"
        };

        if (dialog.ShowDialog(this) != true)
            return;

        var sb = new StringBuilder("Value,Count,Share\r\n");
        foreach (var row in _field.Values)
        {
            sb.Append(EscapeCsv(row.Value)).Append(',')
              .Append(row.Count).Append(',')
              .Append(row.ShareText).AppendLine();
        }

        File.WriteAllText(dialog.FileName, sb.ToString(), new UTF8Encoding(true));
        MessageBox.Show(this, $"已导出：{dialog.FileName}", "导出完成",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private static string EscapeCsv(string value) =>
        value.IndexOfAny([',', '"', '\n', '\r']) < 0
            ? value
            : "\"" + value.Replace("\"", "\"\"") + "\"";

    private static string SanitizeFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "field" : name;
    }
}
