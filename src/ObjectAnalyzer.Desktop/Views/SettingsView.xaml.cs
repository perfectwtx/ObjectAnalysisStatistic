using System.Windows;
using System.Windows.Controls;
using ObjectAnalyzer.Desktop.Services;

namespace ObjectAnalyzer.Desktop.Views;

public partial class SettingsView : UserControl
{
    private bool _suppress;

    public SettingsView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        LocalizationService.LanguageChanged += (_, _) => Dispatcher.Invoke(Refresh);
        ThemeService.ThemeChanged += (_, _) => Dispatcher.Invoke(Refresh);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        Refresh();
    }

    public void Refresh()
    {
        _suppress = true;
        try
        {
            LanguageComboBox.Items.Clear();
            LanguageComboBox.Items.Add(new ComboBoxItem
            {
                Content = LocalizationService.Get("Str.Lang.Zh", "中文"),
                Tag = AppLanguage.Chinese
            });
            LanguageComboBox.Items.Add(new ComboBoxItem
            {
                Content = LocalizationService.Get("Str.Lang.En", "English"),
                Tag = AppLanguage.English
            });
            LanguageComboBox.SelectedIndex = LocalizationService.Current == AppLanguage.English ? 1 : 0;

            ThemeComboBox.Items.Clear();
            foreach (AppThemeMode mode in Enum.GetValues<AppThemeMode>())
            {
                ThemeComboBox.Items.Add(new ComboBoxItem
                {
                    Content = ThemeService.GetModeLabel(mode),
                    Tag = mode
                });
            }

            for (int i = 0; i < ThemeComboBox.Items.Count; i++)
            {
                if (ThemeComboBox.Items[i] is ComboBoxItem item
                    && item.Tag is AppThemeMode mode
                    && mode == ThemeService.Mode)
                {
                    ThemeComboBox.SelectedIndex = i;
                    break;
                }
            }
        }
        finally
        {
            _suppress = false;
        }
    }

    private void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress)
            return;
        if (LanguageComboBox.SelectedItem is not ComboBoxItem item || item.Tag is not AppLanguage language)
            return;
        if (language == LocalizationService.Current)
            return;
        LocalizationService.Apply(language);
        Refresh();
    }

    private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress)
            return;
        if (ThemeComboBox.SelectedItem is not ComboBoxItem item || item.Tag is not AppThemeMode mode)
            return;
        if (mode == ThemeService.Mode)
            return;
        ThemeService.Apply(mode);
        Refresh();
    }
}
