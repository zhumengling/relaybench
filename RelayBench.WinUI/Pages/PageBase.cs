using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using RelayBench.WinUI.Services;
using Windows.System;

namespace RelayBench.WinUI.Pages;

public abstract class PageBase : Page
{
    private ResponsiveLayoutService? _responsiveLayoutService;

    protected PageBase()
    {
        this.NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Enabled;
        Loaded += PageBase_Loaded;
        SizeChanged += PageBase_SizeChanged;
    }

    private void PageBase_Loaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        _responsiveLayoutService ??= ResponsiveLayoutService.Attach(this);
        _responsiveLayoutService.Refresh();
    }

    private void PageBase_SizeChanged(object sender, Microsoft.UI.Xaml.SizeChangedEventArgs e)
        => _responsiveLayoutService?.Refresh();

    protected void FlyoutButton_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not Button button ||
            e.Key is not (VirtualKey.Enter or VirtualKey.Space or VirtualKey.GamepadA or VirtualKey.Application))
        {
            return;
        }

        button.Flyout?.ShowAt(button);
        e.Handled = true;
    }
}
