using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ObjectAnalyzer.Desktop.Services;
using ObjectAnalyzer.Desktop.ViewModels;

namespace ObjectAnalyzer.Desktop.Views;

public partial class OutputView : UserControl
{
    public OutputView() { InitializeComponent(); }
    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.HasOutput != true) return;
        try
        {
            Clipboard.SetText(ViewModel.OutputText);
            ActionStatus.Text = LocalizationService.Get("Str.Output.Copied");
        }
        catch (ExternalException ex) { ActionStatus.Text = ex.Message; }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.HasOutput != true) return;
        var dialog = new SaveFileDialog { Filter = "Text (*.txt)|*.txt", FileName = "analysis-log.txt", DefaultExt = ".txt" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, ViewModel.OutputText);
            ActionStatus.Text = LocalizationService.Get("Str.Output.Saved") + " " + dialog.FileName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ActionStatus.Text = ex.Message;
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        ViewModel?.ClearOutput();
        ActionStatus.Text = LocalizationService.Get("Str.Output.Cleared");
    }

    private void Log_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (ActionStatus is not null) ActionStatus.Text = string.Empty;
        if (FollowOutput?.IsChecked == true) LogTextBox?.ScrollToEnd();
    }

    private void Follow_Checked(object sender, RoutedEventArgs e) => LogTextBox?.ScrollToEnd();
}
