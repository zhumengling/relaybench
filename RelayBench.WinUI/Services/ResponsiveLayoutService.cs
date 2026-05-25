using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace RelayBench.WinUI.Services;

internal sealed class ResponsiveLayoutService
{
    internal const double CompactWidthThreshold = 980d;
    internal const double StackedWidthThreshold = 1320d;
    internal const double VeryCompactWidthThreshold = 720d;
    internal const double LargeMinWidthThreshold = 560d;
    internal const double LargeFixedColumnThreshold = 160d;
    internal const double TallFixedRowThreshold = 240d;
    internal const double ChartHeightThreshold = 170d;
    internal const double CompactChartHeight = 132d;
    internal const double VeryCompactChartHeight = 112d;

    private readonly FrameworkElement _root;
    private readonly Dictionary<FrameworkElement, ElementSnapshot> _elementSnapshots = [];
    private readonly Dictionary<Button, ButtonSnapshot> _buttonSnapshots = [];
    private readonly Dictionary<TextBlock, Visibility> _buttonLabelVisibilities = [];
    private readonly Dictionary<ColumnDefinition, GridLength> _columnWidths = [];
    private readonly Dictionary<RowDefinition, GridLength> _rowHeights = [];
    private readonly Dictionary<ScrollViewer, ScrollSnapshot> _scrollSnapshots = [];
    private readonly Dictionary<Grid, GridStackSnapshot> _gridStackSnapshots = [];
    private readonly double _stackedWidthThreshold;
    private readonly bool _stretchDirectScrollContent;
    private readonly bool _preserveHorizontalOverflowContent;
    private bool _isRefreshQueued;

    private ResponsiveLayoutService(
        FrameworkElement root,
        double stackedWidthThreshold,
        bool stretchDirectScrollContent,
        bool preserveHorizontalOverflowContent)
    {
        _root = root;
        _stackedWidthThreshold = stackedWidthThreshold;
        _stretchDirectScrollContent = stretchDirectScrollContent;
        _preserveHorizontalOverflowContent = preserveHorizontalOverflowContent;
        _root.SizeChanged += Root_SizeChanged;
        _root.Loaded += Root_Loaded;
        _root.Unloaded += Root_Unloaded;
    }

    public static ResponsiveLayoutService Attach(FrameworkElement root)
        => new(root, StackedWidthThreshold, stretchDirectScrollContent: true, preserveHorizontalOverflowContent: false);

    internal static ResponsiveLayoutService AttachDialog(FrameworkElement root)
        => new(root, 820d, stretchDirectScrollContent: false, preserveHorizontalOverflowContent: true);

    public void Refresh()
    {
        _isRefreshQueued = false;
        var width = _root.ActualWidth;
        if (width <= 0)
        {
            return;
        }

        ApplyResponsiveLayout(
            _root,
            width < _stackedWidthThreshold,
            width < CompactWidthThreshold,
            width < VeryCompactWidthThreshold);
    }

    private void Root_Loaded(object sender, RoutedEventArgs e)
        => QueueRefresh();

    private void Root_SizeChanged(object sender, SizeChangedEventArgs e)
        => QueueRefresh();

    private void Root_Unloaded(object sender, RoutedEventArgs e)
    {
        _root.SizeChanged -= Root_SizeChanged;
        _root.Loaded -= Root_Loaded;
        _root.Unloaded -= Root_Unloaded;
    }

    private void QueueRefresh()
    {
        if (_isRefreshQueued)
        {
            return;
        }

        _isRefreshQueued = true;
        if (!_root.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, Refresh))
        {
            Refresh();
        }
    }

    private void ApplyResponsiveLayout(DependencyObject current, bool isStacked, bool isCompact, bool isVeryCompact)
    {
        if (current is FrameworkElement element)
        {
            ApplyElementLayout(element, isStacked, isCompact, isVeryCompact);
        }

        if (current is Grid grid)
        {
            ApplyGridLayout(grid, isStacked, isCompact, isVeryCompact);
        }

        if (current is ScrollViewer scrollViewer)
        {
            ApplyScrollLayout(scrollViewer, isStacked || isCompact);
        }

        var childCount = VisualTreeHelper.GetChildrenCount(current);
        for (var index = 0; index < childCount; index++)
        {
            ApplyResponsiveLayout(VisualTreeHelper.GetChild(current, index), isStacked, isCompact, isVeryCompact);
        }
    }

    private void ApplyElementLayout(FrameworkElement element, bool isStacked, bool isCompact, bool isVeryCompact)
    {
        if (!isStacked && !isCompact)
        {
            RestoreElement(element);
            RestoreCompactButtonLabel(element);
            RestoreButton(element);
            return;
        }

        if (element is Button button)
        {
            ApplyCompactButton(button);
        }

        if (element is TextBlock textBlock && ShouldCompactButtonLabel(textBlock))
        {
            _buttonLabelVisibilities.TryAdd(textBlock, textBlock.Visibility);
            textBlock.Visibility = Visibility.Collapsed;
        }

        if ((isStacked || isCompact) &&
            element.MinWidth >= LargeMinWidthThreshold &&
            !ShouldPreserveHorizontalOverflowContent(element))
        {
            CaptureElement(element);
            element.MinWidth = 0;
        }

        if (_stretchDirectScrollContent && GetDirectPageScrollViewer(element) is not null)
        {
            CaptureElement(element);
            var availableWidth = ResolveAvailableContentWidth(element);
            element.MaxWidth = availableWidth;
            element.Width = availableWidth;
        }

        var originalHeight = _elementSnapshots.TryGetValue(element, out var snapshot)
            ? snapshot.Height
            : element.Height;
        if ((isStacked || isCompact) && IsChartElement(element) && originalHeight >= ChartHeightThreshold)
        {
            CaptureElement(element);
            element.Height = Math.Min(originalHeight, isVeryCompact ? VeryCompactChartHeight : CompactChartHeight);
        }
    }

    private void ApplyGridLayout(Grid grid, bool isStacked, bool isCompact, bool isVeryCompact)
    {
        if (ShouldStackGrid(grid))
        {
            if (isStacked)
            {
                ApplyStackedGridLayout(grid);
                return;
            }

            RestoreStackedGridLayout(grid);
        }

        if (!isCompact)
        {
            RestoreGrid(grid);
            return;
        }

        if (grid.ColumnDefinitions.Count > 1)
        {
            foreach (var column in grid.ColumnDefinitions)
            {
                var originalWidth = _columnWidths.TryGetValue(column, out var capturedWidth)
                    ? capturedWidth
                    : column.Width;

                if (!originalWidth.IsAbsolute || originalWidth.Value < LargeFixedColumnThreshold)
                {
                    continue;
                }

                _columnWidths.TryAdd(column, originalWidth);
                column.Width = new GridLength(1, GridUnitType.Star);
            }
        }

        foreach (var row in grid.RowDefinitions)
        {
            var originalHeight = _rowHeights.TryGetValue(row, out var capturedHeight)
                ? capturedHeight
                : row.Height;

            if (!originalHeight.IsAbsolute || originalHeight.Value < TallFixedRowThreshold)
            {
                continue;
            }

            _rowHeights.TryAdd(row, originalHeight);
            row.Height = new GridLength(isVeryCompact ? 160d : 190d);
        }
    }

    private void ApplyScrollLayout(ScrollViewer scrollViewer, bool isCompact)
    {
        if (!isCompact)
        {
            RestoreScrollViewer(scrollViewer);
            return;
        }

        if (!IsPageAuthoredScrollViewer(scrollViewer))
        {
            return;
        }

        if (ShouldPreserveHorizontalOverflowScrollViewer(scrollViewer))
        {
            return;
        }

        _scrollSnapshots.TryAdd(scrollViewer, new ScrollSnapshot(
            scrollViewer.HorizontalScrollBarVisibility,
            scrollViewer.HorizontalScrollMode,
            scrollViewer.VerticalScrollBarVisibility,
            scrollViewer.VerticalScrollMode));
        ResetHorizontalScrollOffset(scrollViewer);
        scrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        scrollViewer.HorizontalScrollMode = ScrollMode.Disabled;
        scrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        scrollViewer.VerticalScrollMode = ScrollMode.Auto;
        ResetHorizontalScrollOffset(scrollViewer);
    }

    private void RestoreElement(FrameworkElement element)
    {
        if (!_elementSnapshots.TryGetValue(element, out var snapshot))
        {
            return;
        }

        element.MinWidth = snapshot.MinWidth;
        element.MaxWidth = snapshot.MaxWidth;
        element.Height = snapshot.Height;
        element.Width = snapshot.Width;
    }

    private void RestoreCompactButtonLabel(FrameworkElement element)
    {
        if (element is not TextBlock textBlock ||
            !_buttonLabelVisibilities.TryGetValue(textBlock, out var visibility))
        {
            return;
        }

        textBlock.Visibility = visibility;
    }

    private void ApplyCompactButton(Button button)
    {
        _buttonSnapshots.TryAdd(button, new ButtonSnapshot(button.Padding, button.MinWidth));
        button.MinWidth = 0;

        if (!HasIconLabelContent(button))
        {
            return;
        }

        button.Padding = new Thickness(
            Math.Min(button.Padding.Left, 7d),
            Math.Min(button.Padding.Top, 5d),
            Math.Min(button.Padding.Right, 7d),
            Math.Min(button.Padding.Bottom, 5d));
    }

    private void RestoreButton(FrameworkElement element)
    {
        if (element is not Button button ||
            !_buttonSnapshots.TryGetValue(button, out var snapshot))
        {
            return;
        }

        button.Padding = snapshot.Padding;
        button.MinWidth = snapshot.MinWidth;
    }

    private void RestoreGrid(Grid grid)
    {
        foreach (var column in grid.ColumnDefinitions)
        {
            if (_columnWidths.TryGetValue(column, out var originalWidth))
            {
                column.Width = originalWidth;
            }
        }

        foreach (var row in grid.RowDefinitions)
        {
            if (_rowHeights.TryGetValue(row, out var originalHeight))
            {
                row.Height = originalHeight;
            }
        }
    }

    private void RestoreScrollViewer(ScrollViewer scrollViewer)
    {
        if (!_scrollSnapshots.TryGetValue(scrollViewer, out var snapshot))
        {
            return;
        }

        scrollViewer.HorizontalScrollBarVisibility = snapshot.HorizontalScrollBarVisibility;
        scrollViewer.HorizontalScrollMode = snapshot.HorizontalScrollMode;
        scrollViewer.VerticalScrollBarVisibility = snapshot.VerticalScrollBarVisibility;
        scrollViewer.VerticalScrollMode = snapshot.VerticalScrollMode;
    }

    private static void ResetHorizontalScrollOffset(ScrollViewer scrollViewer)
    {
        scrollViewer.ChangeView(0d, null, null, disableAnimation: true);
        scrollViewer.DispatcherQueue.TryEnqueue(
            DispatcherQueuePriority.Low,
            () => scrollViewer.ChangeView(0d, null, null, disableAnimation: true));
    }

    private void ApplyStackedGridLayout(Grid grid)
    {
        if (!_gridStackSnapshots.TryGetValue(grid, out var snapshot))
        {
            snapshot = GridStackSnapshot.Capture(grid);
            _gridStackSnapshots.Add(grid, snapshot);
        }

        if (grid.ColumnDefinitions.Count == 0)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition());
        }

        for (var index = 0; index < grid.ColumnDefinitions.Count; index++)
        {
            grid.ColumnDefinitions[index].Width = index == 0
                ? new GridLength(1, GridUnitType.Star)
                : new GridLength(0);
        }

        var childCount = grid.Children.Count;
        while (grid.RowDefinitions.Count < childCount)
        {
            grid.RowDefinitions.Add(new RowDefinition());
        }

        for (var index = 0; index < grid.RowDefinitions.Count; index++)
        {
            grid.RowDefinitions[index].Height = index < childCount
                ? GridLength.Auto
                : new GridLength(0);
        }

        var orderedChildren = snapshot.Children
            .OrderBy(child => ResolveStackPriority(grid, child))
            .ThenBy(static child => child.Row)
            .ThenBy(static child => child.Column)
            .ThenBy(static child => child.Index)
            .ToArray();

        for (var index = 0; index < orderedChildren.Length; index++)
        {
            var child = orderedChildren[index].Child;
            Grid.SetRow(child, index);
            Grid.SetColumn(child, 0);
            Grid.SetRowSpan(child, 1);
            Grid.SetColumnSpan(child, 1);
        }

        CaptureElement(grid);
        grid.MinWidth = 0;
        if (grid.Name == "Chat工作区Grid" || IsTopLevelResponsiveGrid(grid))
        {
            grid.Width = ResolveAvailableContentWidth(grid);
        }
    }

    private void RestoreStackedGridLayout(Grid grid)
    {
        if (!_gridStackSnapshots.TryGetValue(grid, out var snapshot))
        {
            return;
        }

        for (var index = 0; index < grid.ColumnDefinitions.Count && index < snapshot.ColumnWidths.Length; index++)
        {
            grid.ColumnDefinitions[index].Width = snapshot.ColumnWidths[index];
        }

        for (var index = 0; index < grid.RowDefinitions.Count && index < snapshot.RowHeights.Length; index++)
        {
            grid.RowDefinitions[index].Height = snapshot.RowHeights[index];
        }

        while (grid.RowDefinitions.Count > snapshot.RowHeights.Length)
        {
            grid.RowDefinitions.RemoveAt(grid.RowDefinitions.Count - 1);
        }

        foreach (var child in snapshot.Children)
        {
            Grid.SetRow(child.Child, child.Row);
            Grid.SetColumn(child.Child, child.Column);
            Grid.SetRowSpan(child.Child, child.RowSpan);
            Grid.SetColumnSpan(child.Child, child.ColumnSpan);
        }
    }

    private void CaptureElement(FrameworkElement element)
    {
        _elementSnapshots.TryAdd(element, new ElementSnapshot(element.MinWidth, element.MaxWidth, element.Height, element.Width));
    }

    private static bool ShouldCompactButtonLabel(TextBlock textBlock)
    {
        if (string.IsNullOrWhiteSpace(textBlock.Text))
        {
            return false;
        }

        return VisualTreeHelper.GetParent(textBlock) is StackPanel { Orientation: Orientation.Horizontal } stackPanel &&
               HasFontIconSibling(stackPanel) &&
               HasButtonAncestor(stackPanel);
    }

    private static bool HasIconLabelContent(Button button)
        => button.Content is StackPanel { Orientation: Orientation.Horizontal } stackPanel &&
           HasFontIconSibling(stackPanel);

    private static bool HasFontIconSibling(StackPanel stackPanel)
    {
        foreach (var child in stackPanel.Children)
        {
            if (child is FontIcon)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasButtonAncestor(DependencyObject element)
    {
        var current = element;
        for (var depth = 0; depth < 4; depth++)
        {
            current = VisualTreeHelper.GetParent(current);
            if (current is null)
            {
                return false;
            }

            if (current is Button)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsChartElement(FrameworkElement element)
        => element.GetType().FullName == "LiveChartsCore.SkiaSharpView.WinUI.CartesianChart";

    private static bool ShouldStackGrid(Grid grid)
        => grid.ColumnDefinitions.Count > 1 &&
           HasResponsiveStackTag(grid);

    private static bool HasResponsiveStackTag(Grid grid)
        => grid.Tag is string tag &&
           tag.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
               .Contains("ResponsiveStack", StringComparer.OrdinalIgnoreCase);

    private static int ResolveStackPriority(Grid grid, GridChildSnapshot child)
    {
        if (grid.Name == "Chat工作区Grid")
        {
            return child.Column switch
            {
                1 => 0,
                0 => 1,
                2 => 2,
                _ => child.Column
            };
        }

        return child.Row * 100 + child.Column;
    }

    private static bool IsPageAuthoredScrollViewer(ScrollViewer scrollViewer)
        => scrollViewer.Content is Grid or StackPanel or Border;

    private static bool IsDirectPageScrollContent(FrameworkElement element)
        => GetDirectPageScrollViewer(element) is not null;

    private static ScrollViewer? GetDirectPageScrollViewer(FrameworkElement element)
        => element.Parent is ScrollViewer scrollViewer && IsPageAuthoredScrollViewer(scrollViewer)
            ? scrollViewer
            : null;

    private double ResolveAvailableContentWidth(FrameworkElement element)
    {
        var scrollViewerWidth = GetDirectPageScrollViewer(element)?.ActualWidth ?? 0d;
        var width = scrollViewerWidth > 0 ? scrollViewerWidth : _root.ActualWidth;
        return Math.Max(320d, width - 16d);
    }

    private static bool IsTopLevelResponsiveGrid(Grid grid)
    {
        if (grid.Parent is ScrollViewer)
        {
            return true;
        }

        return grid.Parent is StackPanel stackPanel &&
               IsDirectPageScrollContent(stackPanel);
    }

    private bool ShouldPreserveHorizontalOverflowContent(FrameworkElement element)
        => _preserveHorizontalOverflowContent &&
           FindHorizontalOverflowScrollViewer(element) is not null;

    private bool ShouldPreserveHorizontalOverflowScrollViewer(ScrollViewer scrollViewer)
        => _preserveHorizontalOverflowContent &&
           IsHorizontalOverflowScrollViewer(scrollViewer);

    private static ScrollViewer? FindHorizontalOverflowScrollViewer(DependencyObject element)
    {
        var current = VisualTreeHelper.GetParent(element);
        while (current is not null)
        {
            if (current is ScrollViewer scrollViewer &&
                IsHorizontalOverflowScrollViewer(scrollViewer))
            {
                return scrollViewer;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static bool IsHorizontalOverflowScrollViewer(ScrollViewer scrollViewer)
        => scrollViewer.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled ||
           scrollViewer.HorizontalScrollMode != ScrollMode.Disabled;

    private readonly record struct ElementSnapshot(double MinWidth, double MaxWidth, double Height, double Width);

    private readonly record struct ButtonSnapshot(Thickness Padding, double MinWidth);

    private readonly record struct ScrollSnapshot(
        ScrollBarVisibility HorizontalScrollBarVisibility,
        ScrollMode HorizontalScrollMode,
        ScrollBarVisibility VerticalScrollBarVisibility,
        ScrollMode VerticalScrollMode);

    private sealed record GridStackSnapshot(
        GridLength[] ColumnWidths,
        GridLength[] RowHeights,
        GridChildSnapshot[] Children)
    {
        public static GridStackSnapshot Capture(Grid grid)
            => new(
                grid.ColumnDefinitions.Select(static column => column.Width).ToArray(),
                grid.RowDefinitions.Select(static row => row.Height).ToArray(),
                grid.Children
                    .OfType<FrameworkElement>()
                    .Select(static (child, index) => new GridChildSnapshot(
                        child,
                        index,
                        Grid.GetRow(child),
                        Grid.GetColumn(child),
                        Grid.GetRowSpan(child),
                        Grid.GetColumnSpan(child)))
                    .ToArray());
    }

    private sealed record GridChildSnapshot(
        FrameworkElement Child,
        int Index,
        int Row,
        int Column,
        int RowSpan,
        int ColumnSpan);

}
