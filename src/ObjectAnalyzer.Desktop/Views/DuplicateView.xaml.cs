using System.Windows.Controls;
using System.Windows;
using System.Windows.Input;
using ObjectAnalyzer.Desktop.ViewModels;

namespace ObjectAnalyzer.Desktop.Views;

public partial class DuplicateView : UserControl
{
    public DuplicateView()
    {
        InitializeComponent();
    }

    private void Duplicates_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid || e.OriginalSource is not DependencyObject source
            || ItemsControl.ContainerFromElement(grid, source) is not DataGridRow { Item: DesktopDuplicateRow row })
            return;

        new DuplicateDetailWindow(row) { Owner = Window.GetWindow(this) }.ShowDialog();
        e.Handled = true;
    }
}
