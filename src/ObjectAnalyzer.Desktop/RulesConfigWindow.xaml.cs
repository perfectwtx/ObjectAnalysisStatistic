using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
      // Runtime：控制数据展开与解析行为
      "runtime": {
        "flatten": true
      },

      "fields": {
        "gender": { "enumValues": ["Male", "Female", "Unknown"] },
        "age": { "required": true, "minValue": 0, "maxValue": 120 },
        "email": { "pattern": "\\S+@\\S+", "required": true },
        "id": { "unique": true },

        // 缺失值与显式 null 都会计入空值率
        "address.district": { "nullRateMax": 0.2 },

        // 将字符串先按 JSON 解析，再展开子字段
        "line": { "transform": { "parse": "json" } },
        "line.body": { "transform": { "parse": "json" } }
      },

      // Excel/CSV：指定列按 JSON 解析后再统计
      // "valueParsers": [
      //   { "source": "payload", "parseType": "auto", "flatten": true, "header": 1 }
      // ],

      "expectations": { "minRowCount": 3, "maxDuplicateRate": 0 }
    }
    """;

    private string? _currentPath;
    private readonly ObservableCollection<FieldRuleRow> _fieldRows = new();
    private bool _loadingForm;
    private static readonly JsonSerializerOptions s_formJsonOptions = new() { WriteIndented = true };
    public string? SavedRulesPath { get; private set; }

    public RulesConfigWindow(string? rulesPath)
    {
        InitializeComponent();
        FieldsGrid.ItemsSource = _fieldRows;
        _currentPath = string.IsNullOrWhiteSpace(rulesPath) ? null : rulesPath;
        PathTextBlock.Text = _currentPath is null ? "尚未选择规则文件" : _currentPath;

        if (_currentPath is not null && File.Exists(_currentPath))
        {
            try
            {
                RulesTextBox.Text = File.ReadAllText(_currentPath);
                LoadFormFromJson(RulesTextBox.Text);
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
            LoadFormFromJson(RulesTextBox.Text);
        }
    }

    private void Template_Click(object sender, RoutedEventArgs e)
    {
        if (ConfirmDiscardIfChanged())
        {
            RulesTextBox.Text = Template;
            LoadFormFromJson(RulesTextBox.Text);
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
            LoadFormFromJson(RulesTextBox.Text);
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

    private void AddField_Click(object sender, RoutedEventArgs e)
    {
        _fieldRows.Add(new FieldRuleRow { Name = "newField" });
        FieldsGrid.SelectedIndex = _fieldRows.Count - 1;
        FieldsGrid.ScrollIntoView(FieldsGrid.SelectedItem);
    }

    private void RemoveField_Click(object sender, RoutedEventArgs e)
    {
        if (FieldsGrid.SelectedItem is FieldRuleRow row)
            _fieldRows.Remove(row);
    }

    private void LoadFormFromJson(string text)
    {
        _loadingForm = true;
        try
        {
            var root = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            }) as JsonObject;

            var runtime = root?["runtime"] as JsonObject;
            FlattenCheckBox.IsChecked = runtime?["flatten"]?.GetValue<bool>() ?? true;

            _fieldRows.Clear();
            if (root?["fields"] is JsonObject fields)
            {
                foreach (var item in fields)
                {
                    var rule = item.Value as JsonObject;
                    _fieldRows.Add(new FieldRuleRow
                    {
                        Name = item.Key,
                        Required = rule?["required"]?.GetValue<bool>() ?? false,
                        Unique = rule?["unique"]?.GetValue<bool>() ?? false,
                        MinValue = ReadNumber(rule?["minValue"]),
                        MaxValue = ReadNumber(rule?["maxValue"]),
                        NullRateMax = ReadNumber(rule?["nullRateMax"]),
                        Pattern = rule?["pattern"]?.GetValue<string>() ?? string.Empty
                    });
                }
            }

            var expectations = root?["expectations"] as JsonObject;
            MinRowCountTextBox.Text = expectations?["minRowCount"]?.ToString() ?? string.Empty;
            MaxDuplicateRateTextBox.Text = expectations?["maxDuplicateRate"]?.ToString() ?? string.Empty;
        }
        catch
        {
            // Invalid JSON remains editable in the advanced tab; validation will show the precise error.
        }
        finally
        {
            _loadingForm = false;
        }
    }

    private void SyncFormToJson()
    {
        if (_loadingForm)
            return;

        var root = JsonNode.Parse(RulesTextBox.Text, documentOptions: new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        }) as JsonObject ?? new JsonObject();

        var runtime = root["runtime"] as JsonObject ?? new JsonObject();
        runtime["flatten"] = FlattenCheckBox.IsChecked == true;
        root["runtime"] = runtime;

        var fields = root["fields"] as JsonObject ?? new JsonObject();
        foreach (var row in _fieldRows)
        {
            if (string.IsNullOrWhiteSpace(row.Name))
                throw new InvalidOperationException("字段路径不能为空。");

            var rule = fields[row.Name] as JsonObject ?? new JsonObject();
            rule["required"] = row.Required;
            rule["unique"] = row.Unique;
            SetNumber(rule, "minValue", row.MinValue, row.Name);
            SetNumber(rule, "maxValue", row.MaxValue, row.Name);
            SetNumber(rule, "nullRateMax", row.NullRateMax, row.Name);

            if (string.IsNullOrWhiteSpace(row.Pattern))
                rule.Remove("pattern");
            else
                rule["pattern"] = row.Pattern;

            fields[row.Name] = rule;
        }

        // 表单删除的字段也从 JSON 移除；保留非表单字段规则以免丢失高级配置。
        var visibleNames = _fieldRows.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        var originallySimpleNames = fields
            .Where(x => x.Value is JsonObject obj &&
                        (obj.ContainsKey("required") || obj.ContainsKey("unique") ||
                         obj.ContainsKey("minValue") || obj.ContainsKey("maxValue") ||
                         obj.ContainsKey("nullRateMax") || obj.ContainsKey("pattern")))
            .Select(x => x.Key)
            .ToList();

        // 只删除明确从表格移除、且仅包含表单支持属性的字段。
        foreach (var name in originallySimpleNames.Where(name => !visibleNames.Contains(name)).ToList())
        {
            if (fields[name] is JsonObject obj &&
                obj.All(p => p.Key is "required" or "unique" or "minValue" or "maxValue" or "nullRateMax" or "pattern"))
                fields.Remove(name);
        }

        root["fields"] = fields;

        var expectations = root["expectations"] as JsonObject ?? new JsonObject();
        SetInteger(expectations, "minRowCount", MinRowCountTextBox.Text);
        SetDecimal(expectations, "maxDuplicateRate", MaxDuplicateRateTextBox.Text);
        root["expectations"] = expectations;

        RulesTextBox.Text = root.ToJsonString(s_formJsonOptions);
    }

    private static double? ReadNumber(JsonNode? node) =>
        double.TryParse(node?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value : null;

    private static void SetNumber(JsonObject target, string key, string? text, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            target.Remove(key);
            return;
        }

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
            throw new InvalidOperationException($"字段“{fieldName}”的 {key} 必须是有效数字。");

        if (key == "nullRateMax" && (value < 0 || value > 1))
            throw new InvalidOperationException($"字段“{fieldName}”的 nullRateMax 必须在 0 到 1 之间。");

        target[key] = value;
    }

    private static void SetInteger(JsonObject target, string key, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            target.Remove(key);
            return;
        }

        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < 0)
            throw new InvalidOperationException($"{key} 必须是大于或等于 0 的整数。");

        target[key] = value;
    }

    private static void SetDecimal(JsonObject target, string key, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            target.Remove(key);
            return;
        }

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
            !double.IsFinite(value) || value < 0 || value > 1)
            throw new InvalidOperationException($"{key} 必须是 0 到 1 之间的数字。");

        target[key] = value;
    }

    private sealed class FieldRuleRow
    {
        public string Name { get; set; } = string.Empty;
        public bool Required { get; set; }
        public bool Unique { get; set; }
        public string? MinValue { get; set; }
        public string? MaxValue { get; set; }
        public string? NullRateMax { get; set; }
        public string Pattern { get; set; } = string.Empty;
    }

    private bool ValidateRules(bool showSuccess)
    {
        try
        {
            SyncFormToJson();
        }
        catch (Exception ex)
        {
            ValidationTextBlock.Text = $"表单配置错误：{ex.Message}";
            return false;
        }

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

            // Web 端允许 JSONC 尾随逗号，但 Core 的统一反序列化选项不允许。
            // 本地校验先去除尾随逗号，再用 Core 的权威模型检查字段类型与结构。
            var normalizedText = RemoveTrailingCommas(text);
            var rules = JsonSerializer.Deserialize<AnalysisRules>(normalizedText, AnalysisRules.RulesJsonOptions);
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

    private static string RemoveTrailingCommas(string jsonc)
    {
        // JSON 字符串内部可能包含逗号，扫描时必须跳过字符串与转义字符。
        var result = new StringBuilder(jsonc.Length);
        bool inString = false;
        bool escaped = false;

        for (int i = 0; i < jsonc.Length; i++)
        {
            char ch = jsonc[i];
            if (inString)
            {
                result.Append(ch);
                if (escaped)
                    escaped = false;
                else if (ch == '\\\\')
                    escaped = true;
                else if (ch == '"')
                    inString = false;
                continue;
            }

            if (ch == '"')
            {
                inString = true;
                result.Append(ch);
                continue;
            }

            if (ch == ',')
            {
                int next = i + 1;
                while (next < jsonc.Length)
                {
                    if (char.IsWhiteSpace(jsonc[next]))
                    {
                        next++;
                        continue;
                    }

                    if (jsonc[next] == '/' && next + 1 < jsonc.Length && jsonc[next + 1] == '/')
                    {
                        next += 2;
                        while (next < jsonc.Length && jsonc[next] != '\n' && jsonc[next] != '\r')
                            next++;
                        continue;
                    }

                    if (jsonc[next] == '/' && next + 1 < jsonc.Length && jsonc[next + 1] == '*')
                    {
                        next += 2;
                        while (next + 1 < jsonc.Length && !(jsonc[next] == '*' && jsonc[next + 1] == '/'))
                            next++;
                        next = Math.Min(jsonc.Length, next + 2);
                        continue;
                    }

                    break;
                }

                if (next < jsonc.Length && (jsonc[next] == '}' || jsonc[next] == ']'))
                    continue;
            }

            result.Append(ch);
        }

        return result.ToString();
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
