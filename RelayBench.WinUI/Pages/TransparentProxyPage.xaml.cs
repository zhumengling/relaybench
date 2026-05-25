using LiveChartsCore;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView.WinUI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RelayBench.WinUI.Charts;
using RelayBench.WinUI.Dialogs;
using RelayBench.WinUI.Services;
using RelayBench.WinUI.Storage;
using RelayBench.WinUI.ViewModels;
using System.Text;
using Windows.ApplicationModel.DataTransfer;

namespace RelayBench.WinUI.Pages;

public sealed partial class TransparentProxyPage : PageBase
{
    private const double CompactProxyWidth = 980d;
    private const double VeryCompactProxyWidth = 760d;

    public TransparentProxyViewModel ViewModel { get; } = App.TransparentProxyViewModel;
    private bool _viewModelSubscribed;

    public TransparentProxyPage()
    {
        InitializeComponent();
        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
        ActualThemeChanged += (_, _) => ApplyTrendChartTheme();
        SubscribeViewModel();
        ApplyTrendChartTheme();
        ApplyTrendCharts();
        ApplyCacheHitRing();
        SizeChanged += (_, args) => QueueResponsiveProxyLayout(args.NewSize.Width);
        Loaded += (_, _) => QueueResponsiveProxyLayout(ActualWidth);
    }

    private void QueueResponsiveProxyLayout(double width)
    {
        if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => ApplyResponsiveProxyLayout(width)))
        {
            ApplyResponsiveProxyLayout(width);
        }
    }

    private void ApplyResponsiveProxyLayout(double width)
    {
        if (width <= 0)
        {
            return;
        }

        var isCompact = width < CompactProxyWidth;
        var isVeryCompact = width < VeryCompactProxyWidth;

        ProxySettingsColumn.Width = new GridLength(0);
        ProxySettingsScroll.Visibility = Visibility.Collapsed;
        ProxyChartsColumn.Width = isVeryCompact
            ? new GridLength(240)
            : isCompact
                ? new GridLength(272)
                : new GridLength(320);
        ProxyChartsScroll.Visibility = Visibility.Visible;

        if (isCompact && ViewModel.IsTransparentProxySettingsDrawerOpen)
        {
            ViewModel.IsTransparentProxySettingsDrawerOpen = false;
        }
    }

    private void SubscribeViewModel()
    {
        if (_viewModelSubscribed)
        {
            return;
        }

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModelSubscribed = true;
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        if (!_viewModelSubscribed)
        {
            return;
        }

        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModelSubscribed = false;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(TransparentProxyViewModel.LatencyChartSeries):
            case nameof(TransparentProxyViewModel.LatencyChartYAxes):
            case nameof(TransparentProxyViewModel.LatencyChartXAxes):
                ApplyTrendCharts();
                break;
            case nameof(TransparentProxyViewModel.ThroughputChartSeries):
            case nameof(TransparentProxyViewModel.ThroughputChartYAxes):
            case nameof(TransparentProxyViewModel.ThroughputChartXAxes):
                ApplyTrendCharts();
                break;
            case nameof(TransparentProxyViewModel.CacheHitPercentValue):
                ApplyCacheHitRing();
                break;
        }
    }

    private void ApplyTrendCharts()
    {
        ApplyChartChrome(ThroughputTrendChart, ChartPalette.ResolveTheme(ActualTheme));
        ApplyChartChrome(LatencyTrendChart, ChartPalette.ResolveTheme(ActualTheme));
        LatencyTrendChart.Series = ViewModel.LatencyChartSeries;
        LatencyTrendChart.YAxes = ViewModel.LatencyChartYAxes;
        LatencyTrendChart.XAxes = ViewModel.LatencyChartXAxes;
        ThroughputTrendChart.Series = ViewModel.ThroughputChartSeries;
        ThroughputTrendChart.YAxes = ViewModel.ThroughputChartYAxes;
        ThroughputTrendChart.XAxes = ViewModel.ThroughputChartXAxes;
    }

    private void ApplyTrendChartTheme()
    {
        var theme = ChartPalette.ResolveTheme(ActualTheme);
        ViewModel.SetTrendChartTheme(theme);
        ApplyChartChrome(ThroughputTrendChart, theme);
        ApplyChartChrome(LatencyTrendChart, theme);
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

    private void ApplyCacheHitRing()
    {
        const double dashUnits = 30.9;
        var normalized = Math.Clamp(ViewModel.CacheHitPercentValue, 0, 100) / 100d;
        var filled = dashUnits * normalized;
        var empty = Math.Max(0.1, dashUnits - filled);
        CacheHitRing.StrokeDashArray = new DoubleCollection { filled, empty };
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        SubscribeViewModel();

        // Wire up persistence on load so the proxy view always uses fresh saved state
        ViewModel.SetStrategyRepository(new StrategyRepository());
        await ViewModel.LoadStrategiesAsync();
        ViewModel.SetRouteRepository(new RouteRepository());
        await ViewModel.LoadRoutesAsync();

        // Load recent logs
        if (ViewModel.LogViewer.FilteredEntries.Count == 0)
        {
            await ViewModel.LogViewer.LoadLogsCommand.ExecuteAsync(null);
            ViewModel.RefreshRecentActivityFromLogEntries(ViewModel.LogViewer.FilteredEntries);
        }

        // Initialize OAuth state from persisted credentials
        ViewModel.InitializeOAuthState();
    }

    private async void OnAddRouteClick(object sender, RoutedEventArgs e)
    {
        var dialog = new RouteEditorDialog().UseHostTheme(this);

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary && dialog.Result is not null)
        {
            await ViewModel.AddOrUpdateRouteAsync(dialog.Result);
        }
    }

    private async void OnEditRouteClick(object sender, RoutedEventArgs e)
    {
        if (!TryResolveRoute(sender, out var route)) return;

        await ShowRouteEditorAsync(route);
    }

    private async void OnEditRouteMenuClick(object sender, RoutedEventArgs e)
    {
        if (!TryResolveRoute(sender, out var route)) return;

        await ShowRouteEditorAsync(route);
    }

    private async Task ShowRouteEditorAsync(RouteDefinition route)
    {
        var dialog = new RouteEditorDialog
        {
            ExistingRoute = route
        }.UseHostTheme(this);

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary && dialog.Result is not null)
        {
            await ViewModel.AddOrUpdateRouteAsync(dialog.Result);
        }
    }

    private async void OnRemoveRouteClick(object sender, RoutedEventArgs e)
    {
        if (!TryResolveRoute(sender, out var route)) return;

        await ConfirmRemoveRouteAsync(route);
    }

    private async void OnRemoveRouteMenuClick(object sender, RoutedEventArgs e)
    {
        if (!TryResolveRoute(sender, out var route)) return;

        await ConfirmRemoveRouteAsync(route);
    }

    private async Task ConfirmRemoveRouteAsync(RouteDefinition route)
    {
        var confirmDialog = ConfirmationDialog.CreateDestructive(
            "\u5220\u9664\u8DEF\u7531",
            $"\u786E\u5B9A\u8981\u5220\u9664\u8DEF\u7531 \"{route.Name}\" \u5417\uFF1F",
            "\u6B64\u64CD\u4F5C\u4E0D\u53EF\u64A4\u9500\uFF1B\u5982\u679C\u900F\u660E\u4EE3\u7406\u6B63\u5728\u8FD0\u884C\uFF0C\u65B0\u8DEF\u7531\u961F\u5217\u4F1A\u7ACB\u5373\u751F\u6548\u3002",
            "\u5220\u9664",
            "\u53D6\u6D88");
        confirmDialog.UseHostTheme(this);

        var result = await confirmDialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            await ViewModel.RemoveRouteAsync(route);
        }
    }

    private void OnCopyRouteNameClick(object sender, RoutedEventArgs e)
    {
        if (!TryResolveRoute(sender, out var route)) return;

        SetClipboardText(route.Name);
    }

    private void OnCopyRouteIdClick(object sender, RoutedEventArgs e)
    {
        if (!TryResolveRoute(sender, out var route)) return;

        SetClipboardText(route.Id);
    }

    private void OnCopyRouteUrlClick(object sender, RoutedEventArgs e)
    {
        if (!TryResolveRoute(sender, out var route)) return;

        SetClipboardText(route.UpstreamUrl);
    }

    private void OnCopyRouteMatchClick(object sender, RoutedEventArgs e)
    {
        if (!TryResolveRoute(sender, out var route)) return;

        SetClipboardText(BuildRouteMatchSummary(route));
    }

    private void OnCopyRouteSummaryClick(object sender, RoutedEventArgs e)
    {
        if (!TryResolveRoute(sender, out var route)) return;

        SetClipboardText(BuildRouteSummary(route));
    }

    private void OnCopyRouteStatusClick(object sender, RoutedEventArgs e)
    {
        if (!TryResolveRoute(sender, out var route)) return;

        SetClipboardText(BuildRouteStatusSummary(route));
    }

    private void OnCopyRouteDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        if (!TryResolveRoute(sender, out var route)) return;

        SetClipboardText(BuildRouteDiagnosticsSummary(route));
    }

    private async void OnEnableRouteMenuClick(object sender, RoutedEventArgs e)
    {
        if (!TryResolveRoute(sender, out var route)) return;

        await SetRouteEnabledAsync(route, true);
    }

    private async void OnDisableRouteMenuClick(object sender, RoutedEventArgs e)
    {
        if (!TryResolveRoute(sender, out var route)) return;

        await SetRouteEnabledAsync(route, false);
    }

    private async void OnMoveRouteUpClick(object sender, RoutedEventArgs e)
    {
        if (!TryResolveRoute(sender, out var route)) return;

        await ViewModel.MoveTransparentProxyRouteEditorItemUpCommand.ExecuteAsync(route);
    }

    private async void OnMoveRouteDownClick(object sender, RoutedEventArgs e)
    {
        if (!TryResolveRoute(sender, out var route)) return;

        await ViewModel.MoveTransparentProxyRouteEditorItemDownCommand.ExecuteAsync(route);
    }

    private async void OnMoveRouteUpMenuClick(object sender, RoutedEventArgs e)
    {
        if (!TryResolveRoute(sender, out var route)) return;

        await ViewModel.MoveTransparentProxyRouteEditorItemUpCommand.ExecuteAsync(route);
    }

    private async void OnMoveRouteDownMenuClick(object sender, RoutedEventArgs e)
    {
        if (!TryResolveRoute(sender, out var route)) return;

        await ViewModel.MoveTransparentProxyRouteEditorItemDownCommand.ExecuteAsync(route);
    }

    private async void OnFetchRouteModelsMenuClick(object sender, RoutedEventArgs e)
    {
        if (!TryResolveRoute(sender, out var route)) return;

        await ViewModel.FetchTransparentProxyRouteEditorItemModelsCommand.ExecuteAsync(route);
    }

    private async void OnAddRouteModelMappingMenuClick(object sender, RoutedEventArgs e)
    {
        if (!TryResolveRoute(sender, out var route)) return;

        await ViewModel.AddTransparentProxyRouteModelMappingCommand.ExecuteAsync(route);
    }

    private async void OnRemoveRouteModelMappingMenuClick(object sender, RoutedEventArgs e)
    {
        if (!TryResolveRoute(sender, out var route)) return;

        await ViewModel.RemoveTransparentProxyRouteModelMappingCommand.ExecuteAsync(route);
    }

    private void OnDiscoverRouteProtocolsMenuClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.DiscoverProtocolsCommand.CanExecute(null))
        {
            ViewModel.DiscoverProtocolsCommand.Execute(null);
        }
    }

    private void OnRunSelfTestMenuClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.RunSelfTestCommand.CanExecute(null))
        {
            ViewModel.RunSelfTestCommand.Execute(null);
        }
    }

    private void OnResetRouteCircuitMenuClick(object sender, RoutedEventArgs e)
    {
        if (!TryResolveRoute(sender, out var route)) return;

        ViewModel.ResetTransparentProxyRouteCircuitCommand.Execute(route);
    }

    private async void OnOpenRuntimeRouteSettingsClick(object sender, RoutedEventArgs e)
    {
        if (!TryResolveRouteQueueEntry(sender, out var routeEntry)) return;

        ViewModel.OpenTransparentProxyRuntimeRouteSettingsCommand.Execute(routeEntry);
        if (ViewModel.TransparentProxyRuntimeRouteSettingsRoute is { } route)
        {
            await ShowRouteEditorAsync(route);
        }
    }

    private async void OnOpenProxyTrendDialogClick(object sender, RoutedEventArgs e)
    {
        var dialogTheme = ContentDialogThemeExtensions.ResolveHostTheme(this);
        var dialog = new TransparentProxyTrendDialog(ViewModel, dialogTheme).UseHostTheme(this);
        await dialog.ShowAsync();
    }

    private async void OnOpenFailoverDialogClick(object sender, RoutedEventArgs e)
    {
        var dialog = new TransparentProxyFailoverDialog(ViewModel).UseHostTheme(this);
        await dialog.ShowAsync();
    }

    private async void OnOpenOAuthDialogClick(object sender, RoutedEventArgs e)
    {
        ViewModel.InitializeOAuthState();
        var dialog = new TransparentProxyOAuthDialog(ViewModel).UseHostTheme(this);
        dialog.PrepareForShow();
        await dialog.ShowAsync();
    }

    private async Task SetRouteEnabledAsync(RouteDefinition route, bool enabled)
    {
        if (route.Enabled == enabled)
        {
            return;
        }

        await ViewModel.AddOrUpdateRouteAsync(route with
        {
            Enabled = enabled,
            UpdatedAtUtc = DateTime.UtcNow
        });
    }

    private static bool TryResolveRoute(object sender, out RouteDefinition route)
    {
        route = null!;
        if (sender is FrameworkElement { Tag: RouteDefinition taggedRoute })
        {
            route = taggedRoute;
            return true;
        }

        if (sender is FrameworkElement { DataContext: RouteDefinition contextRoute })
        {
            route = contextRoute;
            return true;
        }

        return false;
    }

    private static bool TryResolveRouteQueueEntry(object sender, out RouteQueueEntry route)
    {
        route = null!;
        if (sender is FrameworkElement { Tag: RouteQueueEntry taggedRoute })
        {
            route = taggedRoute;
            return true;
        }

        if (sender is FrameworkElement { DataContext: RouteQueueEntry contextRoute })
        {
            route = contextRoute;
            return true;
        }

        return false;
    }

    private static void SetClipboardText(string text)
    {
        var dataPackage = new DataPackage();
        dataPackage.SetText(text);
        Clipboard.SetContent(dataPackage);
    }

    private static string BuildRouteSummary(RouteDefinition route)
    {
        StringBuilder builder = new();
        builder.AppendLine($"名称：{route.Name}");
        builder.AppendLine($"上游：{route.UpstreamUrl}");
        builder.AppendLine($"优先级：{route.Priority}");
        builder.AppendLine($"已启用：{route.Enabled}");
        builder.AppendLine($"首选传输 API：{NormalizeRouteField(route.PreferredWireApi)}");
        builder.AppendLine($"认证模式：{NormalizeRouteField(route.AuthMode)}");
        builder.AppendLine($"模型过滤：{NormalizeRouteField(route.ModelFilter)}");
        builder.AppendLine($"前缀：{NormalizeRouteField(route.Prefix)}");
        builder.AppendLine($"出站代理：{NormalizeRouteField(route.OutboundProxy)}");
        return builder.ToString().TrimEnd();
    }

    private static string BuildRouteMatchSummary(RouteDefinition route)
    {
        StringBuilder builder = new();
        builder.AppendLine($"路由 ID：{route.Id}");
        builder.AppendLine($"模型过滤：{NormalizeRouteField(route.ModelFilter)}");
        builder.AppendLine($"前缀：{NormalizeRouteField(route.Prefix)}");
        builder.AppendLine($"排除模型：{NormalizeRouteField(route.ExcludedModelPatterns)}");
        return builder.ToString().TrimEnd();
    }

    private static string BuildRouteStatusSummary(RouteDefinition route)
    {
        StringBuilder builder = new();
        builder.AppendLine($"路由：{route.Name}");
        builder.AppendLine($"已启用：{route.Enabled}");
        builder.AppendLine($"优先级：{route.Priority}");
        builder.AppendLine($"首选传输 API：{NormalizeRouteField(route.PreferredWireApi)}");
        builder.AppendLine($"请求重试：{NormalizeRouteNumber(route.RequestRetry)}");
        builder.AppendLine($"最大重试间隔：{NormalizeRouteNumber(route.MaxRetryIntervalSeconds)}s");
        builder.AppendLine($"模型冷却：{NormalizeRouteNumber(route.ModelCooldownSeconds)}s");
        builder.AppendLine($"更新时间 UTC：{route.UpdatedAtUtc:O}");
        return builder.ToString().TrimEnd();
    }

    private static string BuildRouteDiagnosticsSummary(RouteDefinition route)
    {
        StringBuilder builder = new();
        builder.AppendLine(BuildRouteSummary(route));
        builder.AppendLine($"已配置 Header：{!string.IsNullOrWhiteSpace(route.HeadersText)}");
        builder.AppendLine($"已配置 Payload 规则：{!string.IsNullOrWhiteSpace(route.PayloadRulesText)}");
        builder.AppendLine($"OAuth 提供方：{NormalizeRouteField(route.OAuthProvider)}");
        builder.AppendLine($"OAuth 凭据：{NormalizeRouteField(route.OAuthCredentialId)}");
        builder.AppendLine($"Codex 后端：{NormalizeRouteField(route.CodexBackendBaseUrl)}");
        builder.AppendLine($"Codex 快速模式：{route.CodexOAuthFastMode}");
        return builder.ToString().TrimEnd();
    }

    private static string NormalizeRouteField(string? value)
        => string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();

    private static string NormalizeRouteNumber(int? value)
        => value.HasValue ? value.Value.ToString() : "-";

    private async void OnRouteDragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        await ViewModel.ReorderRoutesAsync();
    }

    private void OnRemoveRewriteRuleClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        if (element.DataContext is not ViewModels.ModelRewriteRule rule) return;
        ViewModel.RemoveModelRewriteRuleCommand.Execute(rule);
    }

}
