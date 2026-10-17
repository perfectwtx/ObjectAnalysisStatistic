using System.Windows;
using ObjectAnalyzer.Desktop.ViewModels;

namespace ObjectAnalyzer.Desktop.Views;

public partial class DuplicateDetailWindow : Window
{
    public DuplicateDetailWindow(DesktopDuplicateRow row)
    {
        InitializeComponent();
        DataContext = row;
    }
}
