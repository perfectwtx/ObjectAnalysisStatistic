using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ObjectAnalyzer.Desktop.Services;

namespace ObjectAnalyzer.Desktop;

public partial class RulesConfigWindow : Window
{
    private const string DefaultFileName = "rules.jsonc";
    private string? _currentPath;
    private RulesEditorDocument? _document;
    private string _loadedFormFingerprint = string.Empty;
    private string _savedText = string.Empty;
    private string _savedFormFingerprint = string.Empty;
    private bool _writingJson;
    private bool _advancedEdited;
    private bool _switchingTab;
    private int _lastTabIndex;

    public string? SavedRulesPath { get; private set; }

    public RulesConfigWindow(string? rulesPath)
    {
        InitializeComponent();
        if (!string.IsNullOrWhiteSpace(rulesPath) && File.Exists(rulesPath))
        {
            try
            {
                LoadEditor(File.ReadAllText(rulesPath), rulesPath);
                return;
            }
            catch (Exception ex)
            {
                LoadEditor(RulesEditorDocument.Template, null);
                ValidationTextBlock.Text = $"读取规则文件失败：{ex.Message}。已打开模板，请另存为规则文件。";
                return;
            }
        }
        LoadEditor(RulesEditorDocument.Template, null);
    }

    private void SetJsonText(string text)
    {
        _writingJson = true;
        try { RulesTextBox.Text = text; }
        finally { _writingJson = false; }
    }

    private void LoadEditor(string text, string? path)
    {
        SetJsonText(text);
        _currentPath = path;
        PathTextBlock.Text = path ?? "尚未选择规则文件";
        if (!TryLoadForm())
            SelectTab(3);
        RememberSavedState();
    }

    private bool TryLoadForm()
    {
        try
        {
            // Parse the whole document first; failed parsing never leaves a partially updated form.
            var document = RulesEditorDocument.Parse(RulesTextBox.Text);
            _document = document;
            FieldsGrid.ItemsSource = document.Fields;
            FlattenCheckBox.IsChecked = document.Flatten;
            MinRowCountTextBox.Text = document.MinRowCount;
            MaxDuplicateRateTextBox.Text = document.MaxDuplicateRate;
            _loadedFormFingerprint = document.FormFingerprint();
            _advancedEdited = false;
            return true;
        }
        catch (Exception ex)
        {
            _advancedEdited = true;
            ValidationTextBlock.Text = $"无法同步表单：{ex.Message}。请在高级 JSONC 中修正。";
            return false;
        }
    }

    private string FormFingerprint()
    {
        if (_document is null)
            return string.Empty;
        _document.Flatten = FlattenCheckBox.IsChecked == true;
        _document.MinRowCount = MinRowCountTextBox.Text;
        _document.MaxDuplicateRate = MaxDuplicateRateTextBox.Text;
        return _document.FormFingerprint();
    }

    private void CommitGridEdits()
    {
        if (!FieldsGrid.CommitEdit(DataGridEditingUnit.Cell, true) ||
            !FieldsGrid.CommitEdit(DataGridEditingUnit.Row, true))
            throw new InvalidOperationException("请先修正字段表格中尚未完成的编辑。");
    }

    private void SyncFormToJson()
    {
        if (_advancedEdited)
            return;
        CommitGridEdits();
        var fingerprint = FormFingerprint();
        if (fingerprint == _loadedFormFingerprint)
            return; // Merely validating or changing tabs preserves comments and JSONC formatting.
        if (_document is null)
            throw new InvalidOperationException("请先从有效 JSONC 同步表单。");
        SetJsonText(_document.ToJson());
        _loadedFormFingerprint = fingerprint;
    }

    private void RulesTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_writingJson && IsLoaded)
            _advancedEdited = true;
    }

    private void SyncJsonToForm_Click(object sender, RoutedEventArgs e)
    {
        if (TryLoadForm())
            ValidationTextBlock.Text = "已从高级 JSONC 刷新表单。";
    }

    private void ConfigTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _switchingTab || !ReferenceEquals(e.OriginalSource, ConfigTabs))
            return;
        try
        {
            if (ConfigTabs.SelectedIndex == 3)
                SyncFormToJson();
            else if (_advancedEdited && !TryLoadForm())
            {
                SelectTab(3);
                return;
            }
            _lastTabIndex = ConfigTabs.SelectedIndex;
        }
        catch (Exception ex)
        {
            ValidationTextBlock.Text = $"表单配置错误：{ex.Message}";
            SelectTab(_lastTabIndex);
        }
    }

    private void SelectTab(int index)
    {
        _switchingTab = true;
        try
        {
            ConfigTabs.SelectedIndex = index;
            _lastTabIndex = index;
        }
        finally { _switchingTab = false; }
    }

    private void AddField_Click(object sender, RoutedEventArgs e)
    {
        if (_document is null)
            return;
        var names = _document.Fields.Select(row => row.Name).ToHashSet(StringComparer.Ordinal);
        var name = "newField";
        for (var suffix = 2; names.Contains(name); suffix++)
            name = "newField" + suffix;
        var newRow = new RulesEditorField { Name = name };
        _document.Fields.Add(newRow);
        FieldsGrid.SelectedItem = newRow;
        FieldsGrid.ScrollIntoView(newRow);
    }

    private void RemoveField_Click(object sender, RoutedEventArgs e)
    {
        if (_document is not null && FieldsGrid.SelectedItem is RulesEditorField row)
            _document.Fields.Remove(row);
    }

    private void Template_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard("载入模板会放弃未保存的修改。是否继续？"))
            return;
        SetJsonText(RulesEditorDocument.Template);
        if (TryLoadForm())
            ValidationTextBlock.Text = "已载入示例模板。请按实际数据字段调整后校验。";
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入规则文件",
            Filter = "规则文件 (*.json;*.jsonc;*.txt)|*.json;*.jsonc;*.txt|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true || !ConfirmDiscard("导入文件会放弃未保存的修改。是否继续？"))
            return;
        try
        {
            var text = File.ReadAllText(dialog.FileName);
            LoadEditor(text, dialog.FileName);
            if (!_advancedEdited)
                ValidationTextBlock.Text = "已导入文件，保存前请执行校验。";
        }
        catch (Exception ex)
        {
            ValidationTextBlock.Text = $"导入失败：{ex.Message}";
        }
    }

    private void Validate_Click(object sender, RoutedEventArgs e) => ValidateRules();

    private bool ValidateRules()
    {
        try
        {
            SyncFormToJson();
            var rules = RulesEditorDocument.Validate(RulesTextBox.Text);
            if (_advancedEdited && !TryLoadForm())
                return false;
            ValidationTextBlock.Text = $"校验通过：发现 {rules.Fields.Count:N0} 条字段规则。";
            return true;
        }
        catch (Exception ex)
        {
            ValidationTextBlock.Text = $"规则校验失败：{ex.Message}";
            return false;
        }
    }

    private void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateRules())
            return;
        var dialog = new SaveFileDialog
        {
            Title = "保存规则文件",
            Filter = "JSONC 规则文件 (*.jsonc)|*.jsonc|JSON 规则文件 (*.json)|*.json",
            FileName = _currentPath is not null ? Path.GetFileName(_currentPath) : DefaultFileName,
            AddExtension = true,
            DefaultExt = ".jsonc"
        };
        if (dialog.ShowDialog(this) == true)
            SaveToPath(dialog.FileName);
    }

    private void SaveAndApply_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateRules())
            return;
        var path = _currentPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ObjectAnalyzer", "rules", DefaultFileName);
        if (!SaveToPath(path))
            return;
        SavedRulesPath = path;
        DialogResult = true;
    }

    private bool SaveToPath(string path)
    {
        string? temporaryPath = null;
        try
        {
            var fullPath = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(fullPath)!;
            Directory.CreateDirectory(directory);
            temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temporaryPath, RulesTextBox.Text, new UTF8Encoding(false));
            File.Move(temporaryPath, fullPath, overwrite: true);
            _currentPath = fullPath;
            PathTextBlock.Text = fullPath;
            RememberSavedState();
            ValidationTextBlock.Text = $"已保存：{fullPath}";
            return true;
        }
        catch (Exception ex)
        {
            ValidationTextBlock.Text = $"保存失败：{ex.Message}";
            return false;
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try { File.Delete(temporaryPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private void RememberSavedState()
    {
        _savedText = RulesTextBox.Text;
        _savedFormFingerprint = FormFingerprint();
    }

    private bool ConfirmDiscard(string message)
    {
        // Bindings update on each edit, so this also detects active grid edits and invalid form input.
        if (RulesTextBox.Text == _savedText && FormFingerprint() == _savedFormFingerprint)
            return true;
        return MessageBox.Show(this, message, "未保存的规则修改",
            MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (DialogResult != true && !ConfirmDiscard("是否放弃未保存的修改并关闭？"))
            e.Cancel = true;
        base.OnClosing(e);
    }
}
