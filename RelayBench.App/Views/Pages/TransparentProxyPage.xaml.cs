using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using RelayBench.App.ViewModels;

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

    private void TransparentProxyPageRoot_OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel ||
            !viewModel.IsTransparentProxySettingsDrawerOpen ||
            e.OriginalSource is not DependencyObject source ||
            IsDescendantOf(source, TransparentProxySettingsDrawerHost))
        {
            return;
        }

        if (viewModel.ToggleTransparentProxySettingsDrawerCommand.CanExecute(null))
        {
            viewModel.ToggleTransparentProxySettingsDrawerCommand.Execute(null);
        }
    }

    private static bool IsDescendantOf(DependencyObject source, DependencyObject ancestor)
    {
        for (var current = source; current is not null; current = GetParent(current))
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    private static DependencyObject? GetParent(DependencyObject source)
        => source is Visual or Visual3D
            ? VisualTreeHelper.GetParent(source)
            : LogicalTreeHelper.GetParent(source);
}
