using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using ObjectAnalyzer.Desktop.Services;

namespace ObjectAnalyzer.Desktop.Views;

public partial class WorkspaceView : UserControl
{
    private bool _suppress;

    private static string DataDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ObjectAnalyzer");

    private static string DatabasePath => Path.Combine(DataDirectory, "object-analyzer.db");

    private static string ReportsDirectory => Path.Combine(DataDirectory, "Reports");

    public WorkspaceView()
    {
        InitializeComponent();
        Loaded += WorkspaceView_Loaded;
        LocalizationService.LanguageChanged += (_, _) => Dispatcher.Invoke(RefreshPreferences);
        ThemeService.ThemeChanged += (_, _) => Dispatcher.Invoke(RefreshPreferences);
    }

    private void WorkspaceView_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= WorkspaceView_Loaded;
        RefreshPaths();
        RefreshPreferences();
    }

    private void RefreshPaths()
    {
        DataDirectoryTextBox.Text = DataDirectory;
        DatabasePathTextBox.Text = DatabasePath;
        ReportsDirectoryTextBox.Text = ReportsDirectory;
        AppVersionTextBlock.Text = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "—";
        DotnetVersionTextBlock.Text = RuntimeInformation.FrameworkDescription;
        OsVersionTextBlock.Text = RuntimeInformation.OSDescription;
        RuntimeTextBlock.Text = RuntimeInformation.RuntimeIdentifier;
    }

    private void RefreshPreferences()
    {
        _suppress = true;
        try
        {
            LangZhRadio.IsChecked = LocalizationService.Current == AppLanguage.Chinese;
            LangEnRadio.IsChecked = LocalizationService.Current == AppLanguage.English;

            ThemeLightRadio.IsChecked = ThemeService.Mode == AppThemeMode.Light;
            ThemeDarkRadio.IsChecked = ThemeService.Mode == AppThemeMode.Dark;
            ThemeSystemRadio.IsChecked = ThemeService.Mode == AppThemeMode.System;
            ThemeEyeCareRadio.IsChecked = ThemeService.Mode == AppThemeMode.EyeCare;
        }
        finally
        {
            _suppress = false;
        }
    }

    private void LanguageRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppress)
            return;

        AppLanguage language = ReferenceEquals(sender, LangEnRadio)
            ? AppLanguage.English
            : AppLanguage.Chinese;

        if (language == LocalizationService.Current)
            return;

        LocalizationService.Apply(language);
    }

    private void ThemeRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppress)
            return;

        AppThemeMode mode =
            ReferenceEquals(sender, ThemeDarkRadio) ? AppThemeMode.Dark :
            ReferenceEquals(sender, ThemeSystemRadio) ? AppThemeMode.System :
            ReferenceEquals(sender, ThemeEyeCareRadio) ? AppThemeMode.EyeCare :
            AppThemeMode.Light;

        if (mode == ThemeService.Mode)
            return;

        ThemeService.Apply(mode);
    }

    private void OpenDataDirectory_Click(object sender, RoutedEventArgs e) =>
        OpenDirectory(DataDirectory);

    private void OpenReportsDirectory_Click(object sender, RoutedEventArgs e) =>
        OpenDirectory(ReportsDirectory);

    private void OpenDatabaseLocation_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(DataDirectory);
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{DatabasePath}\"",
            UseShellExecute = true
        });
    }

    private static void OpenDirectory(string path)
    {
        Directory.CreateDirectory(path);
        var startInfo = new ProcessStartInfo
        {
            FileName = "explorer.exe",
            UseShellExecute = true
        };
        startInfo.ArgumentList.Add(path);
        Process.Start(startInfo);
    }
}
