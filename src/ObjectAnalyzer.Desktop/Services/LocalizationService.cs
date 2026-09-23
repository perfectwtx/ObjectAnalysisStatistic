using System.IO;
using System.Text.Json;
using System.Windows;

namespace ObjectAnalyzer.Desktop.Services;

public enum AppLanguage
{
    Chinese,
    English
}

public static class LocalizationService
{
    private const string LocDictionaryTag = "ObjectAnalyzer.Loc";

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

    public static AppLanguage Current { get; private set; } = AppLanguage.Chinese;

    public static event EventHandler? LanguageChanged;

    public static void Initialize()
    {
        Apply(LoadSavedLanguage(), persist: false);
    }

    public static void Apply(AppLanguage language, bool persist = true)
    {
        Current = language;

        var app = Application.Current;
        if (app is null)
            return;

        var dictionaries = app.Resources.MergedDictionaries;

        for (int i = dictionaries.Count - 1; i >= 0; i--)
        {
            if (dictionaries[i].Source?.OriginalString is string source
                && (source.Contains("Strings.zh-CN.xaml", StringComparison.OrdinalIgnoreCase)
                    || source.Contains("Strings.en-US.xaml", StringComparison.OrdinalIgnoreCase)))
            {
                dictionaries.RemoveAt(i);
            }
            else if (Equals(dictionaries[i]["LocDictionaryTag"], LocDictionaryTag))
            {
                dictionaries.RemoveAt(i);
            }
        }

        string relativePath = language == AppLanguage.English
            ? "Localization/Strings.en-US.xaml"
            : "Localization/Strings.zh-CN.xaml";

        var locDict = new ResourceDictionary
        {
            Source = new Uri(relativePath, UriKind.Relative)
        };
        locDict["LocDictionaryTag"] = LocDictionaryTag;
        dictionaries.Add(locDict);

        if (persist)
            Save(language);

        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    public static string Get(string key, string? fallback = null)
    {
        if (Application.Current?.TryFindResource(key) is string value
            && !string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return fallback ?? key;
    }

    private static AppLanguage LoadSavedLanguage()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return AppLanguage.Chinese;

            var settings = JsonSerializer.Deserialize<UiSettings>(
                File.ReadAllText(SettingsPath),
                s_jsonOptions);

            return string.Equals(settings?.Language, "English", StringComparison.OrdinalIgnoreCase)
                ? AppLanguage.English
                : AppLanguage.Chinese;
        }
        catch
        {
            return AppLanguage.Chinese;
        }
    }

    private static void Save(AppLanguage language)
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

            settings.Language = language.ToString();

            File.WriteAllText(
                SettingsPath,
                JsonSerializer.Serialize(settings, s_jsonOptions));
        }
        catch
        {
            // Preference persistence must not block the UI.
        }
    }

    private sealed class UiSettings
    {
        public string? Theme { get; set; }
        public string? Language { get; set; }
    }
}
