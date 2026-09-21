using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;

namespace ObjectAnalyzer.Desktop.Views;

public partial class WorkspaceView : UserControl
{
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
    }

    private void WorkspaceView_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= WorkspaceView_Loaded;
        Refresh();
    }

    private void Refresh()
    {
        DataDirectoryTextBox.Text = DataDirectory;
        DatabasePathTextBox.Text = DatabasePath;
        ReportsDirectoryTextBox.Text = ReportsDirectory;
        AppVersionTextBlock.Text = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "未设置";
        DotnetVersionTextBlock.Text = RuntimeInformation.FrameworkDescription;
        OsVersionTextBlock.Text = RuntimeInformation.OSDescription;
        RuntimeTextBlock.Text = RuntimeInformation.RuntimeIdentifier;
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