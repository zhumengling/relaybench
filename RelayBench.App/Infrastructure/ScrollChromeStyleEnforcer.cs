using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace RelayBench.App.Infrastructure;

public static class ScrollChromeStyleEnforcer
{
    private const string ScrollBarStyleKey = "WorkbenchScrollBarStyle";
    private const string ScrollViewerStyleKey = "WorkbenchScrollViewerStyle";
    private static bool registered;

    public static void Register()
    {
        if (registered)
        {
            return;
        }

        registered = true;
        EventManager.RegisterClassHandler(
            typeof(ScrollBar),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnScrollBarLoaded));

        EventManager.RegisterClassHandler(
            typeof(ScrollViewer),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnScrollViewerLoaded));
    }

    private static void OnScrollBarLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is ScrollBar scrollBar)
        {
            ApplyStyle(scrollBar, ScrollBarStyleKey);
        }
    }

    private static void OnScrollViewerLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is ScrollViewer scrollViewer)
        {
            scrollViewer.Dispatcher.BeginInvoke(
                DispatcherPriority.Loaded,
                () => ApplyStyle(scrollViewer, ScrollViewerStyleKey));
        }
    }

    private static void ApplyStyle(FrameworkElement element, string key)
    {
        if (element.TryFindResource(key) is Style style &&
            !ReferenceEquals(element.Style, style))
        {
            element.Style = style;
        }
    }
}
