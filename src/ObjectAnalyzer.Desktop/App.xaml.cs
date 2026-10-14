using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using ObjectAnalyzer.Desktop.Services;

namespace ObjectAnalyzer.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        LocalizationService.Initialize();
        ThemeService.Initialize();
        base.OnStartup(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrashReport("WPF Dispatcher exception", e.Exception);
        MessageBox.Show(
            $"界面发生未处理异常，程序已记录诊断信息。\n\n{e.Exception.Message}\n\n日志目录：{GetLogDirectory()}",
            "ObjectAnalyzer 运行异常",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        WriteCrashReport("AppDomain unhandled exception (process may exit)", e.ExceptionObject as Exception
            ?? new Exception(e.ExceptionObject?.ToString() ?? "Unknown unhandled exception"));
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        WriteCrashReport("Unobserved task exception", e.Exception);
        e.SetObserved();
    }

    private static string GetLogDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ObjectAnalyzer", "logs");

    private static void WriteCrashReport(string category, Exception exception)
    {
        try
        {
            string directory = GetLogDirectory();
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "desktop-crash.log");
            string entry = $"[{DateTimeOffset.Now:O}] {category}{Environment.NewLine}{exception}{Environment.NewLine}{new string('-', 80)}{Environment.NewLine}";
            lock (typeof(App))
                File.AppendAllText(path, entry, Encoding.UTF8);
        }
        catch
        {
            // Logging must never cause a second application failure.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ThemeService.Shutdown();
        base.OnExit(e);
    }
}
