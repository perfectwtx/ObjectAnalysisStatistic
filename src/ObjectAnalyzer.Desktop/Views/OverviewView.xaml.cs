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

    private void FieldsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FieldsGrid.SelectedItem is not DesktopFieldRow field)
            return;

        var owner = Window.GetWindow(this);
        var dialog = new FieldValueDetailWindow(field)
        {
            Owner = owner
        };
        dialog.ShowDialog();
    }
}
