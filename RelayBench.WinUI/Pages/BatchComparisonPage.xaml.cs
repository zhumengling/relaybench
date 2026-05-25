using LiveChartsCore;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView.WinUI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using RelayBench.WinUI.Charts;
using RelayBench.WinUI.Dialogs;
using RelayBench.WinUI.Services;
using RelayBench.WinUI.Storage;
using RelayBench.WinUI.ViewModels;
using Windows.System;

namespace RelayBench.WinUI.Pages;

public sealed partial class BatchComparisonPage : PageBase
{
    public BatchComparisonViewModel ViewModel { get; } = new();
    public BatchSiteEditorViewModel SiteEditor => ViewModel.SiteEditor;
    private BatchSiteEditorWindow? _siteEditorWindow;

    public BatchComparisonPage()
    {
        InitializeComponent();
        // Initialize auto-save timer on the UI thread's dispatcher queue
        ViewModel.SiteEditor.InitializeAutoSave(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
        ViewModel.ProxyBatchEditorOpenRequested += OnProxyBatchEditorOpenRequested;
        ViewModel.TransparentProxyRoutesSynced += OnTransparentProxyRoutesSynced;
        ActualThemeChanged += (_, _) =>
        {
            ApplyChartTheme();
            ApplyHeatmapColors();
            _siteEditorWindow?.ApplyTheme(ThemeService.GetCurrentTheme());
        };

        // Subscribe to chart data changes
        ViewModel.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(ViewModel.HasLatencyChart) ||
                e.PropertyName == nameof(ViewModel.LatencyChartSeries))
            {
                ApplyLatencyChart();
            }
            else if (e.PropertyName == nameof(ViewModel.HasThroughputChart) ||
                     e.PropertyName == nameof(ViewModel.ThroughputChartSeries))
            {
                ApplyThroughputChart();
            }
            else if (e.PropertyName == nameof(ViewModel.HeatmapCellTones))
            {
                ApplyHeatmapColors();
            }
        };

        ApplyLatencyChart();
        ApplyThroughputChart();
        ApplyHeatmapColors();
        ApplyChartTheme();
    }

    private void ApplyLatencyChart()
    {
        LatencyChartBorder.Visibility = Visibility.Visible;
        LatencyChart.Series = ViewModel.LatencyChartSeries;
        LatencyChart.XAxes = ViewModel.LatencyChartXAxes;
        LatencyChart.YAxes = ViewModel.LatencyChartYAxes;
        ApplyChartChrome(LatencyChart, ChartPalette.ResolveTheme(ActualTheme));
    }

    private void ApplyThroughputChart()
    {
        ThroughputChartBorder.Visibility = Visibility.Visible;
        ThroughputChart.Series = ViewModel.ThroughputChartSeries;
        ThroughputChart.XAxes = ViewModel.ThroughputChartXAxes;
        ThroughputChart.YAxes = ViewModel.ThroughputChartYAxes;
        ApplyChartChrome(ThroughputChart, ChartPalette.ResolveTheme(ActualTheme));
    }

    private void ApplyChartTheme()
    {
        var theme = ChartPalette.ResolveTheme(ActualTheme);
        ApplyChartChrome(LatencyChart, theme);
        ApplyChartChrome(ThroughputChart, theme);
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

    private void ApplyHeatmapColors()
    {
        HeatmapGrid.Children.Clear();
        var tones = ViewModel.HeatmapCellTones;
        if (tones is null) return;

        for (int row = 0; row < 6; row++)
        {
            for (int col = 0; col < 6; col++)
            {
                int index = row * 6 + col;
                var tone = index < tones.Count ? tones[index] : BatchHeatmapCellTone.Empty;
                var cell = new Border
                {
                    Background = ResolveHeatmapBrush(tone),
                    BorderBrush = ResolveThemeBrush("BatchMutedChipBorderBrush"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(2)
                };
                var description = DescribeHeatmapCell(row, col, index, tone);
                AutomationProperties.SetName(cell, description);
                AutomationProperties.SetLocalizedControlType(cell, "batch stability heatmap cell");
                AutomationProperties.SetHelpText(cell, "Route stability sample bucket.");
                ToolTipService.SetToolTip(cell, description);
                Grid.SetRow(cell, row);
                Grid.SetColumn(cell, col);
                HeatmapGrid.Children.Add(cell);
            }
        }
    }

    private static string DescribeHeatmapCell(int row, int col, int index, BatchHeatmapCellTone tone)
    {
        var status = tone switch
        {
            BatchHeatmapCellTone.Healthy => "healthy",
            BatchHeatmapCellTone.Warning => "warning",
            BatchHeatmapCellTone.Danger => "danger",
            _ => "empty"
        };

        return $"Stability sample {index + 1}, row {row + 1}, column {col + 1}: {status}";
    }

    private Brush ResolveHeatmapBrush(BatchHeatmapCellTone tone)
        => tone switch
        {
            BatchHeatmapCellTone.Healthy => ResolveThemeBrush("BatchHeatmapHealthyBrush"),
            BatchHeatmapCellTone.Warning => ResolveThemeBrush("BatchHeatmapWarningBrush"),
            BatchHeatmapCellTone.Danger => ResolveThemeBrush("BatchHeatmapDangerBrush"),
            _ => ResolveHeatmapFallbackBrush()
        };

    private Brush ResolveHeatmapFallbackBrush()
        => ResolveThemeBrush("BatchHeatmapEmptyBrush");

    private Brush ResolveThemeBrush(string resourceKey)
    {
        if (Resources.TryGetValue(resourceKey, out var localBrush) &&
            localBrush is Brush brush)
        {
            return brush;
        }

        if (Application.Current.Resources.TryGetValue(resourceKey, out var appBrushValue) &&
            appBrushValue is Brush appBrush)
        {
            return appBrush;
        }

        return (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];
    }

    private void SiteGroup_Tapped(object sender, TappedRoutedEventArgs e)
    {
        LoadDraftFromGroup(sender);
    }

    private void SiteGroup_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (ShouldActivateRowFromKeyboard(sender, e) && LoadDraftFromGroup(sender))
        {
            e.Handled = true;
        }
    }

    private bool LoadDraftFromGroup(object sender)
    {
        if (sender is FrameworkElement { DataContext: BatchSiteGroupSummary group })
        {
            ViewModel.SiteEditor.LoadDraftFromGroup(group);
            return true;
        }

        return false;
    }

    private static bool ShouldActivateRowFromKeyboard(object sender, KeyRoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, sender))
        {
            return false;
        }

        return e.Key is VirtualKey.Enter or VirtualKey.Space;
    }

    private async void OnTransparentProxyRoutesSynced(object? sender, EventArgs e)
    {
        App.TransparentProxyViewModel.SetStrategyRepository(new StrategyRepository());
        await App.TransparentProxyViewModel.LoadStrategiesAsync();
        App.TransparentProxyViewModel.SetRouteRepository(new RouteRepository());
        await App.TransparentProxyViewModel.LoadRoutesAsync();
    }

    private void OnProxyBatchEditorOpenRequested(object? sender, EventArgs e)
        => OpenSiteEditorWindow();

    private async void OnOpenTopCandidateApplyClick(object sender, RoutedEventArgs e)
    {
        var candidates = ViewModel.BuildTopCandidateApplicationCandidates();
        var dialogViewModel = new BatchTopCandidateApplyDialogViewModel(candidates);
        dialogViewModel.ConfigureClaudeRelayEndpointResolver(
            (settings, probeResult, sourceName) =>
                App.TransparentProxyViewModel.EnsureClaudeRelayEndpointForClientApplyAsync(
                    settings,
                    probeResult,
                    sourceName));

        var dialog = new BatchTopCandidateApplyDialog(dialogViewModel).UseHostTheme(this);
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary && dialog.CodexHistoryMergeRequested)
        {
            await OpenCodexHistoryMergeDialogAsync(dialogViewModel.ApplicationAccess);
        }
    }

    private async Task OpenCodexHistoryMergeDialogAsync(ApplicationCenterViewModel applicationViewModel)
    {
        var dialog = new CodexHistoryMergeDialog(
            applicationViewModel.CodexHistoryStatusText,
            applicationViewModel.CodexHistoryDetailText).UseHostTheme(this);

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            await applicationViewModel.MergeCodexHistoryAfterConfirmationAsync(dialog.SelectedTarget);
        }
    }

    private void OpenSiteEditorWindow()
    {
        if (_siteEditorWindow is not null)
        {
            _siteEditorWindow.ShowCentered();
            return;
        }

        var dialog = new BatchSiteEditorWindow(ViewModel);
        _siteEditorWindow = dialog;
        dialog.ApplyTheme(ThemeService.GetCurrentTheme());
        dialog.Closed += (_, _) => _siteEditorWindow = null;
        dialog.ShowCentered();
    }
}
