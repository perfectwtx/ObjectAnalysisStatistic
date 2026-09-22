using System.IO;
using System.Text.Json;
using System.Windows;

namespace ObjectAnalyzer.Desktop.Services;

public enum AppTheme
{
    Light,
    Dark
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

    public static AppTheme Current { get; private set; } = AppTheme.Light;

    public static void Initialize()
    {
        Apply(LoadSavedTheme(), persist: false);
    }

    public static AppTheme Toggle()
    {
        var next = Current == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark;
        Apply(next, persist: true);
        return Current;
    }

    public static void Apply(AppTheme theme, bool persist = true)
    {
        Current = theme;

        var app = Application.Current;
        if (app is null)
            return;

        var dictionaries = app.Resources.MergedDictionaries;

        for (int i = dictionaries.Count - 1; i >= 0; i--)
        {
            if (dictionaries[i].Source?.OriginalString is string source
                && (source.Contains("LightTheme.xaml", StringComparison.OrdinalIgnoreCase)
                    || source.Contains("DarkTheme.xaml", StringComparison.OrdinalIgnoreCase)))
            {
                dictionaries.RemoveAt(i);
            }
            else if (Equals(dictionaries[i]["ThemeDictionaryTag"], ThemeDictionaryTag))
            {
                dictionaries.RemoveAt(i);
            }
        }

        string relativePath = theme == AppTheme.Dark
            ? "Themes/DarkTheme.xaml"
            : "Themes/LightTheme.xaml";

        var themeDict = new ResourceDictionary
        {
            Source = new Uri(relativePath, UriKind.Relative)
        };
        themeDict["ThemeDictionaryTag"] = ThemeDictionaryTag;

        // Keep theme colors before styles so DynamicResource resolves correctly.
        dictionaries.Insert(0, themeDict);

        if (persist)
            Save(theme);
    }

    public static string GetToggleLabel() =>
        Current == AppTheme.Dark ? "浅色模式" : "深色模式";

    public static string GetToggleTooltip() =>
        Current == AppTheme.Dark
            ? "切换到浅色主题"
            : "切换到深色主题";

    private static AppTheme LoadSavedTheme()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return AppTheme.Light;

            var settings = JsonSerializer.Deserialize<UiSettings>(
                File.ReadAllText(SettingsPath),
                s_jsonOptions);

            if (settings?.Theme is null)
                return AppTheme.Light;

            return string.Equals(settings.Theme, "Dark", StringComparison.OrdinalIgnoreCase)
                ? AppTheme.Dark
                : AppTheme.Light;
        }
        catch
        {
            return AppTheme.Light;
        }
    }

    private static void Save(AppTheme theme)
    {
        try
        {
            string directory = Path.GetDirectoryName(SettingsPath)
                ?? throw new InvalidOperationException("无法确定 UI 设置存储目录。");

            Directory.CreateDirectory(directory);

            var settings = new UiSettings
            {
                Theme = theme.ToString()
            };

            File.WriteAllText(
                SettingsPath,
                JsonSerializer.Serialize(settings, s_jsonOptions));
        }
        catch
        {
            // 主题偏好属于辅助信息，保存失败不影响主流程。
        }
    }

    private sealed class UiSettings
    {
        public string? Theme { get; set; }
    }
}
