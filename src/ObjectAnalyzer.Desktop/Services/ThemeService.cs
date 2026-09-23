using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace ObjectAnalyzer.Desktop.Services;

public enum AppThemeMode
{
    Light,
    Dark,
    System,
    EyeCare
}

public static class ThemeService
{
    private const string ThemeDictionaryTag = "ObjectAnalyzer.Theme";

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static string SettingsPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ObjectAnalyzer",
            "ui-settings.json");

    public static AppThemeMode Mode { get; private set; } = AppThemeMode.Light;

    public static event EventHandler? ThemeChanged;

    public static void Initialize()
    {
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        Apply(LoadSavedMode(), persist: false);
    }

    public static void Shutdown()
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
    }

    public static void Apply(AppThemeMode mode, bool persist = true)
    {
        Mode = mode;
        ApplyResolvedTheme(mode);

        if (persist)
            Save(mode);

        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    public static string GetModeLabel(AppThemeMode mode) => mode switch
    {
        AppThemeMode.Dark => LocalizationService.Get("Str.Theme.Dark", "深色"),
        AppThemeMode.System => LocalizationService.Get("Str.Theme.System", "跟随系统"),
        AppThemeMode.EyeCare => LocalizationService.Get("Str.Theme.EyeCare", "护眼"),
        _ => LocalizationService.Get("Str.Theme.Light", "浅色")
    };

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (Mode != AppThemeMode.System)
            return;

        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color))
            return;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
            return;

        if (dispatcher.CheckAccess())
            ApplyResolvedTheme(AppThemeMode.System);
        else
            dispatcher.Invoke(() => ApplyResolvedTheme(AppThemeMode.System));
    }

    private static void ApplyResolvedTheme(AppThemeMode mode)
    {
        var app = Application.Current;
        if (app is null)
            return;

        ApplicationTheme wpfUiTheme = mode switch
        {
            AppThemeMode.Dark => ApplicationTheme.Dark,
            AppThemeMode.System => IsSystemDark() ? ApplicationTheme.Dark : ApplicationTheme.Light,
            _ => ApplicationTheme.Light
        };

        try
        {
            ApplicationThemeManager.Apply(wpfUiTheme, WindowBackdropType.Mica);
        }
        catch
        {
        }

        string relativePath = mode switch
        {
            AppThemeMode.Dark => "Themes/DarkTheme.xaml",
            AppThemeMode.EyeCare => "Themes/EyeCareTheme.xaml",
            AppThemeMode.System => IsSystemDark() ? "Themes/DarkTheme.xaml" : "Themes/LightTheme.xaml",
            _ => "Themes/LightTheme.xaml"
        };

        var dictionaries = app.Resources.MergedDictionaries;

        for (int i = dictionaries.Count - 1; i >= 0; i--)
        {
            if (dictionaries[i].Source?.OriginalString is string source
                && (source.Contains("LightTheme.xaml", StringComparison.OrdinalIgnoreCase)
                    || source.Contains("DarkTheme.xaml", StringComparison.OrdinalIgnoreCase)
                    || source.Contains("EyeCareTheme.xaml", StringComparison.OrdinalIgnoreCase)))
            {
                dictionaries.RemoveAt(i);
            }
            else if (Equals(dictionaries[i]["ThemeDictionaryTag"], ThemeDictionaryTag))
            {
                dictionaries.RemoveAt(i);
            }
        }

        var themeDict = new ResourceDictionary
        {
            Source = new Uri(relativePath, UriKind.Relative)
        };
        themeDict["ThemeDictionaryTag"] = ThemeDictionaryTag;
        dictionaries.Add(themeDict);

        ApplyWindowChrome();
    }

    private static void ApplyWindowChrome()
    {
        var app = Application.Current;
        if (app is null)
            return;

        if (app.TryFindResource("Brush.Bg") is not Brush bg)
            return;

        foreach (Window window in app.Windows)
            window.Background = bg;

        if (app.MainWindow is not null)
            app.MainWindow.Background = bg;
    }

    private static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            object? value = key?.GetValue("AppsUseLightTheme");
            if (value is int intValue)
                return intValue == 0;
        }
        catch
        {
        }

        return false;
    }

    private static AppThemeMode LoadSavedMode()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return AppThemeMode.Light;

            var settings = JsonSerializer.Deserialize<UiSettings>(
                File.ReadAllText(SettingsPath),
                s_jsonOptions);

            return settings?.Theme?.ToLowerInvariant() switch
            {
                "dark" => AppThemeMode.Dark,
                "system" => AppThemeMode.System,
                "eyecare" => AppThemeMode.EyeCare,
                _ => AppThemeMode.Light
            };
        }
        catch
        {
            return AppThemeMode.Light;
        }
    }

    private static void Save(AppThemeMode mode)
    {
        try
        {
            string directory = Path.GetDirectoryName(SettingsPath)
                ?? throw new InvalidOperationException("Cannot resolve settings directory.");

            Directory.CreateDirectory(directory);

            UiSettings settings;
            if (File.Exists(SettingsPath))
            {
                settings = JsonSerializer.Deserialize<UiSettings>(
                    File.ReadAllText(SettingsPath),
                    s_jsonOptions) ?? new UiSettings();
            }
            else
            {
                settings = new UiSettings();
            }

            settings.Theme = mode.ToString();

            File.WriteAllText(
                SettingsPath,
                JsonSerializer.Serialize(settings, s_jsonOptions));
        }
        catch
        {
        }
    }

    private sealed class UiSettings
    {
        public string? Theme { get; set; }
        public string? Language { get; set; }
    }
}
