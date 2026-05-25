using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace RelayBench.WinUI.Services;

public static class ContentDialogThemeExtensions
{
    public static T UseHostTheme<T>(this T dialog, FrameworkElement host)
        where T : ContentDialog
    {
        dialog.XamlRoot = host.XamlRoot;
        dialog.RequestedTheme = ResolveHostTheme(host);
        ResponsiveLayoutService.AttachDialog(dialog);
        return dialog;
    }

    public static ElementTheme ResolveHostTheme(FrameworkElement host)
    {
        if (host.RequestedTheme is ElementTheme.Light or ElementTheme.Dark)
        {
            return host.RequestedTheme;
        }

        if (host.ActualTheme is ElementTheme.Light or ElementTheme.Dark)
        {
            return host.ActualTheme;
        }

        return ThemeService.GetCurrentTheme();
    }
}
