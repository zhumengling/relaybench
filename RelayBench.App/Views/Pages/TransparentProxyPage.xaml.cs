using System.Windows;
using System.Windows.Controls;

namespace RelayBench.App.Views.Pages;

public partial class TransparentProxyPage : UserControl
{
    public TransparentProxyPage()
    {
        InitializeComponent();
    }

    private void ToggleTokenMeterButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow mainWindow)
        {
            mainWindow.ToggleFloatingTokenMeterFromUi();
        }
    }
}
