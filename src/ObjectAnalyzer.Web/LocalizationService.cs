using System.Globalization;
using System.Resources;

namespace ObjectAnalyzer.Web;

/// <summary>
/// 轻量本地化服务：基于 resx（默认中文，AppStrings.en.resx 为英文）按当前语言取词，
/// 并提供语言切换与变更通知，供组件订阅后重新渲染。
/// </summary>
public sealed class LocalizationService
{
    private const string ResourceName = "ObjectAnalyzer.Web.Resources.AppStrings";
    private readonly ResourceManager _manager = new(ResourceName, typeof(LocalizationService).Assembly);

    public event Action? OnLanguageChanged;

    public string CurrentLanguage { get; private set; } = "zh-CN";

    public string this[string key] => Get(key);

    public string Get(string key) =>
        _manager.GetString(key, CultureInfo.GetCultureInfo(CurrentLanguage)) ?? key;

    public string Get(string key, params object?[] args)
    {
        string value = Get(key);
        return args.Length > 0
            ? string.Format(CultureInfo.InvariantCulture, value, args)
            : value;
    }

    public void SetLanguage(string culture)
    {
        if (string.IsNullOrWhiteSpace(culture) || CurrentLanguage == culture) return;
        CurrentLanguage = culture;
        OnLanguageChanged?.Invoke();
    }
}
