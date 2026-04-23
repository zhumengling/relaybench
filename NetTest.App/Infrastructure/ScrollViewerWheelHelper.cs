using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace NetTest.App.Infrastructure;

public static class ScrollViewerWheelHelper
{
    public static readonly DependencyProperty EnableNestedWheelRoutingProperty =
        DependencyProperty.RegisterAttached(
            "EnableNestedWheelRouting",
            typeof(bool),
            typeof(ScrollViewerWheelHelper),
            new PropertyMetadata(false, OnEnableNestedWheelRoutingChanged));

    public static bool GetEnableNestedWheelRouting(DependencyObject element)
        => (bool)element.GetValue(EnableNestedWheelRoutingProperty);

    public static void SetEnableNestedWheelRouting(DependencyObject element, bool value)
        => element.SetValue(EnableNestedWheelRoutingProperty, value);

    private static void OnEnableNestedWheelRoutingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer scrollViewer)
        {
            return;
        }

        if ((bool)e.NewValue)
        {
            scrollViewer.PreviewMouseWheel += OnPreviewMouseWheel;
            return;
        }

        scrollViewer.PreviewMouseWheel -= OnPreviewMouseWheel;
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer || e.Handled)
        {
            return;
        }

        if (CanScrollVertically(scrollViewer, e.Delta))
        {
            ScrollVertically(scrollViewer, e.Delta);
            e.Handled = true;
            return;
        }

        var parentScrollViewer = FindVisualParent<ScrollViewer>(scrollViewer);
        if (parentScrollViewer is null)
        {
            return;
        }

        e.Handled = true;
        var reroutedEvent = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = sender
        };
        parentScrollViewer.RaiseEvent(reroutedEvent);
    }

    private static bool CanScrollVertically(ScrollViewer scrollViewer, int delta)
    {
        if (scrollViewer.ScrollableHeight <= 0)
        {
            return false;
        }

        return delta > 0
            ? scrollViewer.VerticalOffset > 0
            : scrollViewer.VerticalOffset < scrollViewer.ScrollableHeight;
    }

    private static void ScrollVertically(ScrollViewer scrollViewer, int delta)
    {
        var lineCount = SystemParameters.WheelScrollLines;
        var stepCount = Math.Max(1, lineCount <= 0 ? 3 : lineCount);

        for (var index = 0; index < stepCount; index++)
        {
            if (delta > 0)
            {
                scrollViewer.LineUp();
            }
            else
            {
                scrollViewer.LineDown();
            }
        }
    }

    private static T? FindVisualParent<T>(DependencyObject? child)
        where T : DependencyObject
    {
        while (child is not null)
        {
            child = VisualTreeHelper.GetParent(child);
            if (child is T match)
            {
                return match;
            }
        }

        return null;
    }
}
