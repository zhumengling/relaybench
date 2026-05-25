using Microsoft.UI.Xaml;

namespace RelayBench.WinUI.Services;

public static class ThemeService
{
    private static ElementTheme s_currentTheme = ElementTheme.Default;

    public static ElementTheme ResolveTheme(string? themeName)
        => string.Equals(themeName, "Light", StringComparison.OrdinalIgnoreCase)
            ? ElementTheme.Light
            : string.Equals(themeName, "Dark", StringComparison.OrdinalIgnoreCase)
                ? ElementTheme.Dark
                : ElementTheme.Default;

    public static void SetTheme(ElementTheme theme)
    {
        s_currentTheme = theme;

        try
        {
            if (App.MainWindow is not null)
            {
                App.MainWindow.ApplyTheme(theme);
                return;
            }

            if (App.MainWindow?.Content is FrameworkElement root)
            {
                root.RequestedTheme = theme;
            }
        }
        catch (TypeInitializationException)
        {
            // Unit tests can construct ViewModels without a WinUI Application.
            // Keep the in-memory theme value available without forcing App startup.
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // WinUI XAML resources are unavailable outside an initialized UI thread.
        }
    }

    public static ElementTheme GetCurrentTheme()
    {
        try
        {
            if (App.MainWindow is not null)
            {
                return App.MainWindow.CurrentThemeForChildWindows;
            }

            if (App.MainWindow?.Content is FrameworkElement root)
            {
                return root.RequestedTheme;
            }
        }
        catch (TypeInitializationException)
        {
            return s_currentTheme;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return s_currentTheme;
        }

        return s_currentTheme;
    }
}
