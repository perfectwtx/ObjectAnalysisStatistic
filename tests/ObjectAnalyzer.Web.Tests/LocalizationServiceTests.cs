namespace ObjectAnalyzer.Web.Tests;

/// <summary>本地化服务 (resx 取词 + 语言切换 + 格式化) 的单元测试。</summary>
public class LocalizationServiceTests
{
    [Fact]
    public void Get_DefaultChinese_ReturnsChineseText()
    {
        var svc = new LocalizationService();

        Assert.Equal("开始分析", svc["StartAnalysis"]);
        Assert.Equal("对象字段分析统计", svc["AppBrandSub"]);
        Assert.Equal("请先选择数据文件。", svc["ErrSelectDataFile"]);
        Assert.Equal("切换到深色主题", svc["ThemeDarkTitle"]);
        Assert.Equal("切换到浅色主题", svc["ThemeLightTitle"]);
    }

    [Fact]
    public void Get_English_ReturnsEnglishText()
    {
        var svc = new LocalizationService();
        svc.SetLanguage("en");

        Assert.Equal("Start Analysis", svc["StartAnalysis"]);
        Assert.Equal("Object field analysis & statistics", svc["AppBrandSub"]);
        Assert.Equal("Please select a data file first.", svc["ErrSelectDataFile"]);
        Assert.Equal("Switch to dark theme", svc["ThemeDarkTitle"]);
        Assert.Equal("Switch to light theme", svc["ThemeLightTitle"]);
    }

    [Fact]
    public void Get_WithArgs_FormatsPlaceholders()
    {
        var svc = new LocalizationService();

        Assert.Equal("前 8 行 / 4 列", svc.Get("PreviewNote", 8, 4));

        svc.SetLanguage("en");
        Assert.Equal("First 8 rows / 4 columns", svc.Get("PreviewNote", 8, 4));
    }

    [Fact]
    public void Get_UnknownKey_ReturnsKeyAsFallback()
    {
        var svc = new LocalizationService();

        Assert.Equal("No_Such_Key", svc["No_Such_Key"]);
    }

    [Fact]
    public void SetLanguage_NotifiesOncePerActualChange()
    {
        var svc = new LocalizationService();
        int calls = 0;
        svc.OnLanguageChanged += () => calls++;

        svc.SetLanguage("en");
        svc.SetLanguage("en");
        svc.SetLanguage("zh-CN");

        Assert.Equal(2, calls);
    }
}
