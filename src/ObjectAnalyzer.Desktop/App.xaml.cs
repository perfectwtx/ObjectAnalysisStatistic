using System.Windows;
using ObjectAnalyzer.Desktop.Services;

namespace ObjectAnalyzer.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        ThemeService.Initialize();
        base.OnStartup(e);
    }
}
