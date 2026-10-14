using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ObjectAnalyzer.Desktop.ViewModels;

namespace ObjectAnalyzer.Desktop.Views;

public partial class OverviewView : UserControl
{
    public OverviewView()
    {
        InitializeComponent();
    }

    private void FieldCards_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ItemsControl itemsControl)
            return;

        var container = ItemsControl.ContainerFromElement(
            itemsControl,
            e.OriginalSource as DependencyObject);

        if (container is not FrameworkElement { DataContext: DesktopFieldRow field })
            return;

        var dialog = new FieldValueDetailWindow(field)
        {
            Owner = Window.GetWindow(this)
        };
        dialog.ShowDialog();
    }
}
