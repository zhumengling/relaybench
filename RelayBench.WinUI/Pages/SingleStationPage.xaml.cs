using LiveChartsCore;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RelayBench.WinUI.Charts;
using RelayBench.Services.Infrastructure;
using RelayBench.WinUI.Dialogs;
using RelayBench.WinUI.Services;
using RelayBench.WinUI.Storage;
using RelayBench.WinUI.ViewModels;

namespace RelayBench.WinUI.Pages;

public sealed partial class SingleStationPage : PageBase
{
    private const double SidePanelCollapseWidthThreshold = 980d;
    private const double CompactCommandWidthThreshold = 1500d;
    private const double CompactResultsWidthThreshold = 980d;
    private const double VeryCompactResultsWidthThreshold = 740d;
    private const double FullChartHeight = 184d;
    private const double CompactChartHeight = 132d;
    private const double VeryCompactChartHeight = 112d;

    public SingleStationViewModel ViewModel { get; } = new();
    private bool _chartSyncQueued;
    private bool _isSidePanelInline;

    public SingleStationPage()
    {
        InitializeComponent();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.ProxyEndpointHistoryOpenRequested += OnProxyEndpointHistoryOpenRequested;
        ViewModel.ProxyMultiModelPickerOpenRequested += OnProxyMultiModelPickerOpenRequested;
        Loaded += OnLoaded;
        SizeChanged += OnPageSizeChanged;
        ActualThemeChanged += (_, _) => ApplyChartTheme();
        SyncCharts();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            _ = DispatcherQueue.TryEnqueue(() => OnViewModelPropertyChanged(sender, e));
            return;
        }

        try
        {
            switch (e.PropertyName)
            {
                case nameof(SingleStationViewModel.StabilityChartSeries):
                case nameof(SingleStationViewModel.StabilityChartYAxes):
                case nameof(SingleStationViewModel.StabilityChartXAxes):
                    StabilityChart.Series = ViewModel.StabilityChartSeries;
                    StabilityChart.YAxes = ViewModel.StabilityChartYAxes;
                    StabilityChart.XAxes = ViewModel.StabilityChartXAxes;
                    break;
                case nameof(SingleStationViewModel.ConcurrencyChartSeries):
                case nameof(SingleStationViewModel.ConcurrencyChartYAxes):
                case nameof(SingleStationViewModel.ConcurrencyChartXAxes):
                    ConcurrencyChart.Series = ViewModel.ConcurrencyChartSeries;
                    ConcurrencyChart.YAxes = ViewModel.ConcurrencyChartYAxes;
                    ConcurrencyChart.XAxes = ViewModel.ConcurrencyChartXAxes;
                    break;
                case nameof(SingleStationViewModel.QuickLatencyChartSeries):
                case nameof(SingleStationViewModel.QuickLatencyChartYAxes):
                case nameof(SingleStationViewModel.QuickLatencyChartXAxes):
                    QuickLatencyChart.Series = ViewModel.QuickLatencyChartSeries;
                    QuickLatencyChart.YAxes = ViewModel.QuickLatencyChartYAxes;
                    QuickLatencyChart.XAxes = ViewModel.QuickLatencyChartXAxes;
                    CompactQuickLatencyChart.Series = ViewModel.QuickLatencyChartSeries;
                    CompactQuickLatencyChart.YAxes = ViewModel.QuickLatencyChartYAxes;
                    CompactQuickLatencyChart.XAxes = ViewModel.QuickLatencyChartXAxes;
                    break;
                case nameof(SingleStationViewModel.QuickTtftChartSeries):
                case nameof(SingleStationViewModel.QuickTtftChartYAxes):
                case nameof(SingleStationViewModel.QuickTtftChartXAxes):
                    QuickTtftChart.Series = ViewModel.QuickTtftChartSeries;
                    QuickTtftChart.YAxes = ViewModel.QuickTtftChartYAxes;
                    QuickTtftChart.XAxes = ViewModel.QuickTtftChartXAxes;
                    break;
                case nameof(SingleStationViewModel.QuickThroughputChartSeries):
                case nameof(SingleStationViewModel.QuickThroughputChartYAxes):
                case nameof(SingleStationViewModel.QuickThroughputChartXAxes):
                    QuickThroughputChart.Series = ViewModel.QuickThroughputChartSeries;
                    QuickThroughputChart.YAxes = ViewModel.QuickThroughputChartYAxes;
                    QuickThroughputChart.XAxes = ViewModel.QuickThroughputChartXAxes;
                    break;
                case nameof(SingleStationViewModel.StreamingTokenChartSeries):
                case nameof(SingleStationViewModel.StreamingTokenChartYAxes):
                case nameof(SingleStationViewModel.StreamingTokenChartXAxes):
                    StreamingTokenChart.Series = ViewModel.StreamingTokenChartSeries;
                    StreamingTokenChart.YAxes = ViewModel.StreamingTokenChartYAxes;
                    StreamingTokenChart.XAxes = ViewModel.StreamingTokenChartXAxes;
                    break;
                case nameof(SingleStationViewModel.IsQuickModeVisible):
                case nameof(SingleStationViewModel.IsStabilityModeVisible):
                case nameof(SingleStationViewModel.IsDeepModeVisible):
                case nameof(SingleStationViewModel.IsConcurrencyModeVisible):
                case nameof(SingleStationViewModel.SelectedTestMode):
                    ApplyResponsiveLayout(ActualWidth);
                    QueueChartSync();
                    break;
                case nameof(SingleStationViewModel.IsTesting):
                    UpdateTestingAnimation();
                    break;
            }
        }
        catch (Exception ex)
        {
            AppDiagnosticLog.Write($"SingleStationPage.PropertyChanged.{e.PropertyName}", ex);
        }
    }

    private void OnLoaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        UpdateTestingAnimation();
        OnLoaded_SetupModelComboBox();
        ApplyChartTheme();
        ApplyResponsiveLayout(ActualWidth);
    }

    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
        => ApplyResponsiveLayout(e.NewSize.Width);

    private void ApplyResponsiveLayout(double width)
    {
        if (width <= 0)
        {
            return;
        }

        var isSidePanelCollapsed = width < SidePanelCollapseWidthThreshold;
        var isCommandCompact = width < CompactCommandWidthThreshold;
        var isCompact = width < CompactResultsWidthThreshold;
        var isVeryCompact = width < VeryCompactResultsWidthThreshold;
        var showQuickResults = ViewModel.IsQuickModeVisible;

        SingleStationRootGrid.ColumnSpacing = isSidePanelCollapsed ? 0 : 10;
        SidePanelColumn.Width = isSidePanelCollapsed ? new GridLength(0) : new GridLength(294);
        ApplySidePanelPlacement(isSidePanelCollapsed);

        TopCommandScrollViewer.Visibility = isCommandCompact ? Visibility.Collapsed : Visibility.Visible;
        CompactCommandGrid.Visibility = isCommandCompact ? Visibility.Visible : Visibility.Collapsed;
        ConfigureCompactCommandGrid(isCompact);
        StatusSummaryScrollViewer.Visibility = isCompact ? Visibility.Collapsed : Visibility.Visible;
        CompactStatusSummaryGrid.Visibility = isCompact ? Visibility.Visible : Visibility.Collapsed;

        KpiGrid.MinWidth = isCompact ? 0 : 760;
        QuickChartsGrid.MinWidth = isCompact ? 0 : 1060;
        QuickChartsGrid.Visibility = showQuickResults && !isCompact ? Visibility.Visible : Visibility.Collapsed;
        CompactQuickResultsPanel.Visibility = showQuickResults && isCompact ? Visibility.Visible : Visibility.Collapsed;
        ConfigureFourCardGrid(CompactStatusSummaryGrid, CompactStatusCard, CompactSuccessCard, CompactLatencyCard, CompactThroughputCard, isCompact);
        ConfigureFourCardGrid(CompactQuickMetricGrid, CompactQuickTotalCard, CompactQuickResponseCard, CompactQuickTtftCard, CompactQuickModelCard, isCompact);

        MainContentScrollViewer.HorizontalScrollMode = ScrollMode.Disabled;
        MainContentScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        MainContentScrollViewer.VerticalScrollMode = ScrollMode.Enabled;
        MainContentScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        MainContentScrollViewer.MaxHeight = double.PositiveInfinity;

        QuickLatencyChart.Height = FullChartHeight;
        QuickTtftChart.Height = FullChartHeight;
        QuickThroughputChart.Height = FullChartHeight;
        StreamingTokenChart.Height = FullChartHeight;
        CompactQuickLatencyChart.Height = isVeryCompact ? VeryCompactChartHeight : CompactChartHeight;

        UpdateChartLayouts();
    }

    private void ApplySidePanelPlacement(bool inline)
    {
        if (_isSidePanelInline != inline)
        {
            if (inline)
            {
                SidePanelScrollViewer.Content = null;
                if (!ReferenceEquals(SidePanel.Parent, MainSections))
                {
                    MainSections.Children.Add(SidePanel);
                }
            }
            else
            {
                if (SidePanel.Parent is StackPanel parent)
                {
                    parent.Children.Remove(SidePanel);
                }

                if (!ReferenceEquals(SidePanelScrollViewer.Content, SidePanel))
                {
                    SidePanelScrollViewer.Content = SidePanel;
                }
            }

            _isSidePanelInline = inline;
        }

        SidePanelScrollViewer.Visibility = inline ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetColumnSpan(MainContentScrollViewer, inline ? 2 : 1);
    }

    private void ConfigureCompactCommandGrid(bool isCompact)
    {
        if (CompactCommandGrid.RowDefinitions.Count >= 3)
        {
            CompactCommandGrid.RowDefinitions[2].Height = isCompact ? new GridLength(1, GridUnitType.Auto) : new GridLength(0);
        }

        CompactCommandModelColumn.Width = isCompact ? new GridLength(0) : new GridLength(170);
        Grid.SetColumnSpan(CompactApiPasswordBox, isCompact ? 4 : 2);
        PlaceCard(CompactModelComboBox, isCompact ? 0 : 2, isCompact ? 2 : 1);
        Grid.SetColumnSpan(CompactModelComboBox, isCompact ? 3 : 1);
        PlaceCard(CompactFetchModelsButton, 3, isCompact ? 2 : 1);
    }

    private static void ConfigureFourCardGrid(
        Grid grid,
        FrameworkElement first,
        FrameworkElement second,
        FrameworkElement third,
        FrameworkElement fourth,
        bool isVeryCompact)
    {
        if (grid.ColumnDefinitions.Count >= 4)
        {
            grid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            grid.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
            grid.ColumnDefinitions[2].Width = isVeryCompact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            grid.ColumnDefinitions[3].Width = isVeryCompact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        }

        if (grid.RowDefinitions.Count >= 2)
        {
            grid.RowDefinitions[1].Height = isVeryCompact ? new GridLength(1, GridUnitType.Auto) : new GridLength(0);
        }

        grid.RowSpacing = isVeryCompact ? 8 : 0;
        PlaceCard(first, 0, 0);
        PlaceCard(second, 1, 0);
        PlaceCard(third, isVeryCompact ? 0 : 2, isVeryCompact ? 1 : 0);
        PlaceCard(fourth, isVeryCompact ? 1 : 3, isVeryCompact ? 1 : 0);
    }

    private static void PlaceCard(FrameworkElement element, int column, int row)
    {
        Grid.SetColumn(element, column);
        Grid.SetRow(element, row);
    }

    private void SyncCharts()
    {
        StabilityChart.Series = ViewModel.StabilityChartSeries;
        StabilityChart.YAxes = ViewModel.StabilityChartYAxes;
        StabilityChart.XAxes = ViewModel.StabilityChartXAxes;
        ConcurrencyChart.Series = ViewModel.ConcurrencyChartSeries;
        ConcurrencyChart.YAxes = ViewModel.ConcurrencyChartYAxes;
        ConcurrencyChart.XAxes = ViewModel.ConcurrencyChartXAxes;
        QuickLatencyChart.Series = ViewModel.QuickLatencyChartSeries;
        QuickLatencyChart.YAxes = ViewModel.QuickLatencyChartYAxes;
        QuickLatencyChart.XAxes = ViewModel.QuickLatencyChartXAxes;
        CompactQuickLatencyChart.Series = ViewModel.QuickLatencyChartSeries;
        CompactQuickLatencyChart.YAxes = ViewModel.QuickLatencyChartYAxes;
        CompactQuickLatencyChart.XAxes = ViewModel.QuickLatencyChartXAxes;
        QuickTtftChart.Series = ViewModel.QuickTtftChartSeries;
        QuickTtftChart.YAxes = ViewModel.QuickTtftChartYAxes;
        QuickTtftChart.XAxes = ViewModel.QuickTtftChartXAxes;
        QuickThroughputChart.Series = ViewModel.QuickThroughputChartSeries;
        QuickThroughputChart.YAxes = ViewModel.QuickThroughputChartYAxes;
        QuickThroughputChart.XAxes = ViewModel.QuickThroughputChartXAxes;
        StreamingTokenChart.Series = ViewModel.StreamingTokenChartSeries;
        StreamingTokenChart.YAxes = ViewModel.StreamingTokenChartYAxes;
        StreamingTokenChart.XAxes = ViewModel.StreamingTokenChartXAxes;
        ApplyChartTheme();
    }

    private void ApplyChartTheme()
    {
        var theme = ChartPalette.ResolveTheme(ActualTheme);
        ApplyChartChrome(QuickLatencyChart, theme);
        ApplyChartChrome(CompactQuickLatencyChart, theme);
        ApplyChartChrome(QuickTtftChart, theme);
        ApplyChartChrome(QuickThroughputChart, theme);
        ApplyChartChrome(StreamingTokenChart, theme);
        ApplyChartChrome(StabilityChart, theme);
        ApplyChartChrome(ConcurrencyChart, theme);
    }

    private static void ApplyChartChrome(CartesianChart chart, ElementTheme theme)
    {
        chart.LegendPosition = LegendPosition.Hidden;
        chart.TooltipPosition = TooltipPosition.Top;
        chart.LegendTextPaint = ChartPalette.LegendPaint(theme);
        chart.TooltipTextPaint = ChartPalette.LegendPaint(theme);
        chart.TooltipBackgroundPaint = ChartPalette.TooltipBackgroundPaint(theme);
        chart.AnimationsSpeed = TimeSpan.FromMilliseconds(220);
        chart.EasingFunction = EasingFunctions.CubicOut;
    }

    private void QueueChartSync()
    {
        if (_chartSyncQueued)
        {
            return;
        }

        _chartSyncQueued = true;
        var queued = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            try
            {
                _chartSyncQueued = false;
                SyncCharts();
                UpdateChartLayouts();
            }
            catch (Exception ex)
            {
                AppDiagnosticLog.Write("SingleStationPage.QueueChartSync", ex);
            }
        });
        if (!queued)
        {
            _chartSyncQueued = false;
        }
    }

    private void UpdateChartLayouts()
    {
        QuickLatencyChart.UpdateLayout();
        CompactQuickLatencyChart.UpdateLayout();
        QuickTtftChart.UpdateLayout();
        QuickThroughputChart.UpdateLayout();
        StreamingTokenChart.UpdateLayout();
        StabilityChart.UpdateLayout();
        ConcurrencyChart.UpdateLayout();
    }

    private void UpdateTestingAnimation()
    {
        if (ViewModel.IsTesting)
        {
            TestingPulseStoryboard.Begin();
        }
        else
        {
            TestingPulseStoryboard.Stop();
            TestingGlow.Opacity = 0;
        }
    }

    private void OnLoaded_SetupModelComboBox()
    {
        var textBox = FindDescendant<Microsoft.UI.Xaml.Controls.TextBox>(ModelComboBox);
        if (textBox is not null)
        {
            textBox.Tapped += (s, args) =>
            {
                if (!ModelComboBox.IsDropDownOpen)
                {
                    ModelComboBox.IsDropDownOpen = true;
                }
            };
        }
    }

    private static T? FindDescendant<T>(Microsoft.UI.Xaml.DependencyObject parent) where T : Microsoft.UI.Xaml.DependencyObject
    {
        var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
            {
                return match;
            }

            var result = FindDescendant<T>(child);
            if (result is not null)
            {
                return result;
            }
        }

        return null;
    }

    private void OnOpenEndpointHistoryDialogClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        => ViewModel.OpenProxyEndpointHistoryCommand.Execute(null);

    private async void OnProxyEndpointHistoryOpenRequested(object? sender, EventArgs e)
        => await OpenEndpointHistoryDialogAsync();

    private async Task OpenEndpointHistoryDialogAsync()
    {
        var store = new EndpointHistoryStore();
        var items = await store.LoadAsync();
        var dialog = new EndpointHistoryDialog(items, "Single Station").UseHostTheme(this);
        var result = await dialog.ShowAsync();
        if (dialog.ClearRequested)
        {
            await store.ClearAsync();
            ViewModel.EndpointHistory.Clear();
            return;
        }

        if (result == Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary && dialog.Result is not null)
        {
            ViewModel.ApplyProxyEndpointHistoryItemCommand.Execute(dialog.Result);
        }
    }

    private async void OnProxyMultiModelPickerOpenRequested(object? sender, EventArgs e)
        => await OpenProxyMultiModelPickerAsync();

    private async Task OpenProxyMultiModelPickerAsync()
    {
        var dialog = new MultiModelSelectionDialog(
            ViewModel.GetMultiModelCandidateModels(),
            ViewModel.GetSelectedMultiModelBenchmarkModelNames(),
            ViewModel.Model).UseHostTheme(this);

        var result = await dialog.ShowAsync();
        if (result == Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary)
        {
            ViewModel.ConfirmProxyMultiModelPickerCommand.Execute(dialog.Result);
        }
        else if (result == Microsoft.UI.Xaml.Controls.ContentDialogResult.Secondary)
        {
            ViewModel.SetMultiModelBenchmarkModels([]);
        }
        else
        {
            ViewModel.CloseProxyModelPickerCommand.Execute(null);
        }
    }
}
