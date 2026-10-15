using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using ObjectAnalyzer.Analysis;
using ObjectAnalyzer.Analysis.Rules;

namespace ObjectAnalyzer.Desktop;

public partial class RulesConfigWindow : Window
{
    private const string DefaultFileName = "rules.jsonc";

    private const string Template = """
    {
      // Runtime：解析和运行时行为
      "runtime": {
        "flatten": true,
        "csvInferNumbers": true
      },

      // 字段规则：字段路径可以使用点号，例如 address.city
      "fields": {
        "id": {
          "required": true,
          "unique": true
        },
        "age": {
          "primaryType": "Number",
          "required": true,
          "minValue": 0,
          "maxValue": 120
        },
        "email": {
          "pattern": "^[^@]+@[^@]+$",
          "nullRateMax": 0.05
        }
      },

      // 数据集级质量期望
      "expectations": {
        "minRowCount": 1,
        "maxDuplicateRate": 0.05
      }

      // 可选：valueParsers 可配置 CSV/Excel 列的 JSON 解析规则
    }
    """;

    private string? _currentPath;
    public string? SavedRulesPath { get; private set; }

    public RulesConfigWindow(string? rulesPath)
    {
        InitializeComponent();
        _currentPath = string.IsNullOrWhiteSpace(rulesPath) ? null : rulesPath;
        PathTextBlock.Text = _currentPath is null ? "尚未选择规则文件" : _currentPath;

        if (_currentPath is not null && File.Exists(_currentPath))
        {
            try
            {
                RulesTextBox.Text = File.ReadAllText(_currentPath);
            }
            catch (Exception ex)
            {
                ValidationTextBlock.Text = $"读取规则文件失败：{ex.Message}";
                RulesTextBox.Text = Template;
            }
        }
        else
        {
            RulesTextBox.Text = Template;
        }
    }

    private void Template_Click(object sender, RoutedEventArgs e)
    {
        if (ConfirmDiscardIfChanged())
        {
            RulesTextBox.Text = Template;
            ValidationTextBlock.Text = "已载入示例模板。请按实际数据字段调整后校验。";
        }
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入规则文件",
            Filter = "规则文件 (*.json;*.jsonc;*.txt)|*.json;*.jsonc;*.txt|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            RulesTextBox.Text = File.ReadAllText(dialog.FileName);
            _currentPath = dialog.FileName;
            PathTextBlock.Text = _currentPath;
            ValidationTextBlock.Text = "已导入文件，保存前请执行校验。";
        }
        catch (Exception ex)
        {
            ValidationTextBlock.Text = $"导入失败：{ex.Message}";
        }
    }

    private void Validate_Click(object sender, RoutedEventArgs e) => ValidateRules(showSuccess: true);

    private bool ValidateRules(bool showSuccess)
    {
        string text = RulesTextBox.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            ValidationTextBlock.Text = "规则内容不能为空。";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });

            var unknowns = RulesLint.Scan(text);
            if (unknowns.Count > 0)
            {
                ValidationTextBlock.Text = RulesLint.Describe(unknowns);
                return false;
            }

            var rules = JsonSerializer.Deserialize<AnalysisRules>(text, AnalysisRules.RulesJsonOptions);
            if (rules is null)
            {
                ValidationTextBlock.Text = "规则 JSON 未能解析为有效配置。";
                return false;
            }

            ValidationTextBlock.Text = $"校验通过：发现 {rules.Fields.Count:N0} 条字段规则。";
            return true;
        }
        catch (JsonException ex)
        {
            ValidationTextBlock.Text = $"JSON 语法错误：{ex.Message}";
            return false;
        }
        catch (Exception ex)
        {
            ValidationTextBlock.Text = $"规则校验失败：{ex.Message}";
            return false;
        }
    }

    private void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateRules(showSuccess: false))
            return;

        var dialog = new SaveFileDialog
        {
            Title = "保存规则文件",
            Filter = "JSONC 规则文件 (*.jsonc)|*.jsonc|JSON 规则文件 (*.json)|*.json",
            FileName = _currentPath is not null ? Path.GetFileName(_currentPath) : DefaultFileName,
            AddExtension = true,
            DefaultExt = ".jsonc"
        };

        if (dialog.ShowDialog(this) != true)
            return;

        SaveToPath(dialog.FileName);
    }

    private void SaveAndApply_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateRules(showSuccess: false))
            return;

        string path = _currentPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ObjectAnalyzer", "rules", DefaultFileName);

        if (!SaveToPath(path))
            return;

        SavedRulesPath = path;
        DialogResult = true;
    }

    private bool SaveToPath(string path)
    {
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(path, RulesTextBox.Text.Trim() + Environment.NewLine, new UTF8Encoding(false));
            _currentPath = path;
            PathTextBlock.Text = path;
            ValidationTextBlock.Text = $"已保存：{path}";
            return true;
        }
        catch (Exception ex)
        {
            ValidationTextBlock.Text = $"保存失败：{ex.Message}";
            return false;
        }
    }

    private bool ConfirmDiscardIfChanged()
    {
        if (string.IsNullOrWhiteSpace(RulesTextBox.Text) || RulesTextBox.Text == Template)
            return true;

        return MessageBox.Show(
            this,
            "载入模板会覆盖当前编辑区内容。是否继续？",
            "确认载入模板",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question) == MessageBoxResult.Yes;
    }
}
