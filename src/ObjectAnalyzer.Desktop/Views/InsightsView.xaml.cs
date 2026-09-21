using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using ObjectAnalyzer.Desktop.ViewModels;

namespace ObjectAnalyzer.Desktop.Views;

public partial class InsightsView : UserControl
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    public InsightsView()
    {
        InitializeComponent();
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private void ShowLineage_Click(object sender, RoutedEventArgs e)
    {
        var relationships = ViewModel?.Result?.ObjectRelationships;
        ResultTextBox.Text = relationships is not { Count: > 0 }
            ? "数据血缘\r\n\r\n当前结果未生成对象关系。"
            : string.Join(Environment.NewLine, relationships.Select(x =>
                $"{x.Kind,-18} {x.Path}  出现 {x.OccurrenceCount:N0} 次，非空 {x.NonNullCount:N0}，目标字段 {string.Join(", ", x.TargetFields)}"));
    }

    private void ShowImpact_Click(object sender, RoutedEventArgs e)
    {
        var relationships = ViewModel?.Result?.ObjectRelationships;
        ResultTextBox.Text = relationships is not { Count: > 0 }
            ? "影响分析\r\n\r\n当前结果未生成对象关系。"
            : "影响分析（基于当前本地结果）\r\n\r\n" + string.Join(
                Environment.NewLine,
                relationships.Select(x =>
                    $"• {x.Path} → {string.Join(", ", x.TargetFields)} | {x.Kind} | {x.OccurrenceCount:N0} 次"));
    }

    private void ShowSchemaImpact_Click(object sender, RoutedEventArgs e) =>
        ResultTextBox.Text = MainViewModel.GetInsight("Schema Impact", ViewModel?.Result?.SchemaDiff);

    private void ShowRegression_Click(object sender, RoutedEventArgs e) =>
        ResultTextBox.Text = MainViewModel.GetInsight("质量回归 / 分布漂移", ViewModel?.Result?.DistributionDiff);

    public void ShowEvaluation(object evaluation) =>
        ResultTextBox.Text = JsonSerializer.Serialize(evaluation, s_jsonOptions);
}