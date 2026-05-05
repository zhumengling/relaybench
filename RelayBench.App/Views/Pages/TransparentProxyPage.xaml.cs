using System.Linq;
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
            IsDescendantOf(source, TransparentProxySettingsDrawerHost) ||
            HasOpenSettingsComboBox())
        {
            return;
        }

        if (viewModel.ToggleTransparentProxySettingsDrawerCommand.CanExecute(null))
        {
            viewModel.ToggleTransparentProxySettingsDrawerCommand.Execute(null);
        }
    }

    private void TransparentProxyPageRoot_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape ||
            DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        if (viewModel.IsTransparentProxyLogDetailOpen &&
            viewModel.CloseTransparentProxyLogDetailCommand.CanExecute(null))
        {
            viewModel.CloseTransparentProxyLogDetailCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (viewModel.IsTransparentProxyRouteSettingsOpen &&
            viewModel.CloseTransparentProxyRouteSettingsCommand.CanExecute(null))
        {
            viewModel.CloseTransparentProxyRouteSettingsCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (viewModel.IsTransparentProxySettingsDrawerOpen &&
            viewModel.ToggleTransparentProxySettingsDrawerCommand.CanExecute(null))
        {
            viewModel.ToggleTransparentProxySettingsDrawerCommand.Execute(null);
            e.Handled = true;
        }
    }

    private bool HasOpenSettingsComboBox()
        => FindOpenComboBox(TransparentProxySettingsDrawerHost);

    private static bool FindOpenComboBox(DependencyObject source)
    {
        if (source is ComboBox { IsDropDownOpen: true })
        {
            return true;
        }

        if (source is Visual or Visual3D)
        {
            var childCount = VisualTreeHelper.GetChildrenCount(source);
            for (var index = 0; index < childCount; index++)
            {
                if (FindOpenComboBox(VisualTreeHelper.GetChild(source, index)))
                {
                    return true;
                }
            }
        }

        foreach (var child in LogicalTreeHelper.GetChildren(source).OfType<DependencyObject>())
        {
            if (FindOpenComboBox(child))
            {
                return true;
            }
        }

        return false;
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
