using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RelayBench.Core.Services;
using RelayBench.Services;
using RelayBench.WinUI.Services;
using RelayBench.WinUI.Storage;
using RelayBench.WinUI.ViewModels;

namespace RelayBench.WinUI.Dialogs;

public sealed partial class TransparentProxyOAuthDialog : ContentDialog
{
    private readonly ResponsiveLayoutService _responsiveLayout;
    private RouteDefinition? _editingRoute;
    private ProviderManagerRouteItem? _selectedRouteItem;
    private bool _isApplyingProviderTemplate;
    private bool _isClosing;
    private double _lastRootWidth;
    private double _lastRootHeight;

    public TransparentProxyOAuthDialog(TransparentProxyViewModel viewModel)
    {
        ViewModel = viewModel;
        RouteItems = new ObservableCollection<ProviderManagerRouteItem>();
        OAuthAccountItems = new ObservableCollection<ProviderManagerOAuthAccountItem>();
        InitializeComponent();
        _responsiveLayout = ResponsiveLayoutService.AttachDialog(ProviderManagerSurface);
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
        SizeChanged += OnDialogSizeChanged;
        ViewModel.Routes.CollectionChanged += OnViewModelCollectionChanged;
        ViewModel.ProviderAccounts.CollectionChanged += OnViewModelCollectionChanged;
    }

    public TransparentProxyViewModel ViewModel { get; }

    public ObservableCollection<ProviderManagerRouteItem> RouteItems { get; }

    public ObservableCollection<ProviderManagerOAuthAccountItem> OAuthAccountItems { get; }

    public void PrepareForShow()
    {
        _isClosing = false;
        FitDialogSurface();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        FitDialogSurface();
        InitializeProviderForm();
        RefreshDerivedCollections();

        if (ProviderNavigation.MenuItems.Count > 0)
        {
            ProviderNavigation.SelectedItem = ProviderNavigation.MenuItems[0];
        }

        ShowSection("proxy-settings");
    }

    private void OnClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
        => _isClosing = true;

    private void OnClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        ViewModel.Routes.CollectionChanged -= OnViewModelCollectionChanged;
        ViewModel.ProviderAccounts.CollectionChanged -= OnViewModelCollectionChanged;
        Closing -= OnClosing;
        SizeChanged -= OnDialogSizeChanged;
    }

    private void OnDialogSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_isClosing)
        {
            FitDialogSurface();
        }
    }

    private void FitDialogSurface()
    {
        var rootSize = XamlRoot?.Size;
        var rootWidth = rootSize?.Width > 0 ? rootSize.Value.Width : _lastRootWidth;
        var rootHeight = rootSize?.Height > 0 ? rootSize.Value.Height : _lastRootHeight;
        if (rootWidth <= 0)
        {
            rootWidth = 1280d;
        }

        if (rootHeight <= 0)
        {
            rootHeight = 900d;
        }

        _lastRootWidth = rootWidth;
        _lastRootHeight = rootHeight;
        var horizontalInset = rootWidth < 760d ? 32d : 112d;
        var scrollWidth = Math.Min(1208d, Math.Max(360d, rootWidth - horizontalInset));
        var surfaceWidth = Math.Max(320d, scrollWidth - 28d);

        Width = double.NaN;
        MinWidth = 0d;
        MaxWidth = Math.Max(360d, rootWidth - 16d);
        Resources["ContentDialogMaxWidth"] = MaxWidth;
        RenderTransform = new Microsoft.UI.Xaml.Media.TranslateTransform
        {
            X = Math.Max(0d, (rootWidth - 1280d) / 2d)
        };
        ProviderManagerScroll.Width = scrollWidth;
        ProviderManagerScroll.MaxWidth = scrollWidth;
        ProviderManagerSurface.Width = surfaceWidth;
        ProviderManagerSurface.MaxWidth = surfaceWidth;
        ProviderManagerScroll.MaxHeight = Math.Clamp(rootHeight - 136d, 420d, 820d);
        var useCompactNavigation = rootWidth < 1100d;
        ProviderNavigation.PaneDisplayMode = useCompactNavigation
            ? NavigationViewPaneDisplayMode.LeftCompact
            : NavigationViewPaneDisplayMode.Left;
        ProviderNavigation.IsPaneOpen = !useCompactNavigation;
        _responsiveLayout.Refresh();
    }

    private void InitializeProviderForm()
    {
        if (ProviderKindBox.SelectedIndex < 0)
        {
            ProviderKindBox.SelectedIndex = 0;
        }

        if (ProviderWireApiBox.SelectedIndex < 0)
        {
            ProviderWireApiBox.SelectedIndex = 0;
        }

        if (double.IsNaN(ProviderPriorityBox.Value))
        {
            ProviderPriorityBox.Value = 90;
        }

        ApplyProviderKindTemplate(force: true);
        ShowRouteDetails();
    }

    private void OnViewModelCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => RefreshDerivedCollections(_selectedRouteItem?.Route.Id);

    private void RefreshDerivedCollections(string? preferredRouteId = null)
    {
        var selectedId = preferredRouteId ?? _selectedRouteItem?.Route.Id;

        RouteItems.Clear();
        foreach (var route in ViewModel.Routes
                     .OrderByDescending(static route => route.Enabled)
                     .ThenByDescending(static route => route.Priority)
                     .ThenBy(static route => route.Name, StringComparer.OrdinalIgnoreCase))
        {
            RouteItems.Add(new ProviderManagerRouteItem(route));
        }

        OAuthAccountItems.Clear();
        foreach (var account in ViewModel.ProviderAccounts.Where(static account => account.IsOAuthCredential))
        {
            OAuthAccountItems.Add(new ProviderManagerOAuthAccountItem(account));
        }

        _selectedRouteItem = RouteItems.FirstOrDefault(item =>
            string.Equals(item.Route.Id, selectedId, StringComparison.OrdinalIgnoreCase)) ??
            RouteItems.FirstOrDefault();
        ProviderRouteList.SelectedItem = _selectedRouteItem;
        ProviderRouteList.Visibility = RouteItems.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ProviderEmptyState.Visibility = RouteItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowRouteDetails();
    }

    private void OnProviderNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is string tag)
        {
            ShowSection(tag);
        }
    }

    private void ShowSection(string tag)
    {
        ProxySettingsSection.Visibility = tag == "proxy-settings" ? Visibility.Visible : Visibility.Collapsed;
        ProvidersSection.Visibility = tag == "providers" ? Visibility.Visible : Visibility.Collapsed;
        OAuthSection.Visibility = tag == "oauth" ? Visibility.Visible : Visibility.Collapsed;
        StrategySection.Visibility = tag == "strategy" ? Visibility.Visible : Visibility.Collapsed;
        AliasesSection.Visibility = tag == "aliases" ? Visibility.Visible : Visibility.Collapsed;
        StatusSection.Visibility = tag == "status" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnRouteSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedRouteItem = ProviderRouteList.SelectedItem as ProviderManagerRouteItem;
        ShowRouteDetails();
    }

    private void ShowRouteDetails()
    {
        ProviderFormPanel.Visibility = Visibility.Collapsed;
        RouteDetailsPanel.Visibility = Visibility.Visible;

        var item = _selectedRouteItem;
        var hasSelection = item is not null;
        RouteDetailTitle.Text = item?.Name ?? "选择一个提供商";
        RouteDetailSubtitle.Text = hasSelection
            ? item!.Endpoint
            : "左侧选择提供商后可以编辑、停用、拉取模型或重置冷却。";
        RouteDetailKind.Text = item?.KindName ?? "-";
        RouteDetailProtocol.Text = item?.ProtocolName ?? "-";
        RouteDetailEndpoint.Text = item?.Endpoint ?? "-";
        RouteDetailModels.Text = item?.ModelsText ?? "-";
        RouteDetailApiKey.Text = item?.ApiKeyText ?? "-";
        RouteDetailPriority.Text = item?.PriorityText ?? "-";
        RouteDetailPrefix.Text = item?.PrefixText ?? "-";
        RouteDetailStatus.Text = item?.StatusText ?? "-";
        ToggleRouteButtonText.Text = item?.Route.Enabled == true ? "停用" : "启用";

        var canEditApiRoute = hasSelection && item!.CanEditAsApiKeyRoute;
        EditRouteButton.IsEnabled = canEditApiRoute;
        ToggleRouteButton.IsEnabled = hasSelection;
        FetchModelsButton.IsEnabled = canEditApiRoute;
        ResetCircuitButton.IsEnabled = hasSelection;
        RemoveRouteButton.IsEnabled = hasSelection;
    }

    private void ShowProviderForm(RouteDefinition? route = null, string? providerKind = null)
    {
        _editingRoute = route;
        ProviderFormPanel.Visibility = Visibility.Visible;
        RouteDetailsPanel.Visibility = Visibility.Collapsed;

        if (route is null)
        {
            ResetProviderFields();
            SelectComboBoxTag(ProviderKindBox, providerKind ?? "openai-compatible");
            ApplyProviderKindTemplate(force: true);
            ProviderFormTitle.Text = "新增提供商";
            ProviderFormSubtitle.Text = "选择上游类型后填写地址、Key 和模型。";
            ProviderSaveText.Text = "保存提供商";
            ProviderFormStatus.Text = "保存后会写入透明代理路由，应用接入和协议探测会复用这份配置。";
        }
        else
        {
            LoadRouteIntoForm(route);
        }

        ProviderNameBox.Focus(FocusState.Programmatic);
    }

    private void OnStartAddProviderClick(object sender, RoutedEventArgs e)
        => ShowProviderForm(providerKind: "openai-compatible");

    private void OnQuickAddProviderClick(object sender, RoutedEventArgs e)
    {
        var kind = sender is FrameworkElement { Tag: string tag } ? tag : "openai-compatible";
        ShowProviderForm(providerKind: kind);
    }

    private void OnCancelProviderFormClick(object sender, RoutedEventArgs e)
    {
        _editingRoute = null;
        ShowRouteDetails();
    }

    private void OnEditSelectedRouteClick(object sender, RoutedEventArgs e)
    {
        if (_selectedRouteItem is not { CanEditAsApiKeyRoute: true } item)
        {
            ProviderFormStatus.Text = "OAuth 路由请在 OAuth 与配额页管理。";
            return;
        }

        ShowProviderForm(item.Route);
    }

    private async void OnToggleSelectedRouteEnabledClick(object sender, RoutedEventArgs e)
    {
        if (_selectedRouteItem is null)
        {
            return;
        }

        var route = _selectedRouteItem.Route;
        var updated = route with
        {
            Enabled = !route.Enabled,
            UpdatedAtUtc = DateTime.UtcNow
        };
        await ViewModel.AddOrUpdateRouteAsync(updated);
        RefreshDerivedCollections(updated.Id);
    }

    private async void OnFetchSelectedRouteModelsClick(object sender, RoutedEventArgs e)
    {
        if (_selectedRouteItem is not { CanEditAsApiKeyRoute: true } item)
        {
            return;
        }

        await ViewModel.FetchTransparentProxyRouteEditorItemModelsCommand.ExecuteAsync(item.Route);
        ProviderFormStatus.Text = ViewModel.StatusText;
        RefreshDerivedCollections(item.Route.Id);
    }

    private void OnResetSelectedRouteCircuitClick(object sender, RoutedEventArgs e)
    {
        if (_selectedRouteItem is null)
        {
            return;
        }

        ViewModel.ResetRouteCircuit(_selectedRouteItem.Route.Id);
        ProviderFormStatus.Text = ViewModel.StatusText;
    }

    private async void OnRemoveSelectedRouteClick(object sender, RoutedEventArgs e)
    {
        if (_selectedRouteItem is null)
        {
            return;
        }

        var route = _selectedRouteItem.Route;
        await ViewModel.RemoveRouteAsync(route);
        _selectedRouteItem = null;
        RefreshDerivedCollections();
    }

    private void OnProviderKindChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_editingRoute is not null)
        {
            return;
        }

        ApplyProviderKindTemplate(force: false);
    }

    private async void OnSaveProviderClick(object sender, RoutedEventArgs e)
    {
        var name = ProviderNameBox.Text.Trim();
        var baseUrl = ProviderBaseUrlBox.Text.Trim();
        var apiKey = ProviderApiKeyBox.Password.Trim();
        var prefix = NullIfWhiteSpace(ProviderPrefixBox.Text);
        var models = NormalizeDelimitedText(ProviderModelsBox.Text);
        var preferredWireApi = ReadComboBoxTag(ProviderWireApiBox) ?? DefaultWireApiForKind(ReadProviderKind());
        var priority = ReadPriority();

        if (string.IsNullOrWhiteSpace(name))
        {
            ProviderFormStatus.Text = "请先填写提供商名称。";
            ProviderNameBox.Focus(FocusState.Programmatic);
            return;
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            ProviderFormStatus.Text = "请填写有效的上游 API 地址，必须以 http:// 或 https:// 开头。";
            ProviderBaseUrlBox.Focus(FocusState.Programmatic);
            return;
        }

        if (string.IsNullOrWhiteSpace(apiKey) && string.IsNullOrWhiteSpace(_editingRoute?.ApiKeyProtected))
        {
            ProviderFormStatus.Text = "API Key 不能为空；OAuth 账号请使用“OAuth 与配额”页管理。";
            ProviderApiKeyBox.Focus(FocusState.Programmatic);
            return;
        }

        var id = _editingRoute?.Id ?? TransparentProxyRouteTextCodec.BuildRouteId(name, baseUrl, prefix ?? string.Empty);
        var route = new RouteDefinition(
            Id: id,
            Name: name,
            UpstreamUrl: baseUrl,
            ApiKeyProtected: string.IsNullOrWhiteSpace(apiKey) ? _editingRoute?.ApiKeyProtected : apiKey,
            Priority: priority,
            ModelFilter: models,
            Enabled: ProviderEnabledSwitch.IsOn,
            UpdatedAtUtc: DateTime.UtcNow,
            Prefix: prefix,
            OutboundProxy: _editingRoute?.OutboundProxy,
            RequestRetry: _editingRoute?.RequestRetry,
            MaxRetryIntervalSeconds: _editingRoute?.MaxRetryIntervalSeconds,
            ModelCooldownSeconds: _editingRoute?.ModelCooldownSeconds,
            ExcludedModelPatterns: _editingRoute?.ExcludedModelPatterns,
            PayloadRulesText: _editingRoute?.PayloadRulesText,
            PreferredWireApi: preferredWireApi,
            HeadersText: _editingRoute?.HeadersText,
            AuthMode: TransparentProxyRouteAuthModes.ApiKey);

        await ViewModel.AddOrUpdateRouteAsync(route);
        _editingRoute = null;
        RefreshDerivedCollections(route.Id);
        ShowRouteDetails();
    }

    private void OnRefreshProviderViewClick(object sender, RoutedEventArgs e)
    {
        ViewModel.InitializeOAuthState();
        RefreshDerivedCollections(_selectedRouteItem?.Route.Id);
    }

    private void OnResetQueueRouteCircuitClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: RouteQueueEntry entry })
        {
            ViewModel.ResetRouteCircuit(entry.RouteId);
        }
    }

    private async void OnRefreshOAuthAccountClick(object sender, RoutedEventArgs e)
    {
        if (TryResolveOAuthAccount(sender, out var account))
        {
            await ViewModel.RefreshCodexOAuthCredentialCommand.ExecuteAsync(account);
            RefreshDerivedCollections(_selectedRouteItem?.Route.Id);
        }
    }

    private async void OnDisableOAuthAccountClick(object sender, RoutedEventArgs e)
    {
        if (TryResolveOAuthAccount(sender, out var account))
        {
            await ViewModel.DisableCodexOAuthCredentialCommand.ExecuteAsync(account);
            RefreshDerivedCollections(_selectedRouteItem?.Route.Id);
        }
    }

    private async void OnExportOAuthAccountClick(object sender, RoutedEventArgs e)
    {
        if (TryResolveOAuthAccount(sender, out var account))
        {
            await ViewModel.ExportCodexOAuthCredentialCommand.ExecuteAsync(account);
            RefreshDerivedCollections(_selectedRouteItem?.Route.Id);
        }
    }

    private async void OnDeleteOAuthAccountClick(object sender, RoutedEventArgs e)
    {
        if (TryResolveOAuthAccount(sender, out var account))
        {
            await ViewModel.DeleteCodexOAuthCredentialCommand.ExecuteAsync(account);
            RefreshDerivedCollections(_selectedRouteItem?.Route.Id);
        }
    }

    private void OnRemoveRewriteRuleClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ModelRewriteRule rule })
        {
            ViewModel.RemoveModelRewriteRuleCommand.Execute(rule);
        }
    }

    private void LoadRouteIntoForm(RouteDefinition route)
    {
        _editingRoute = route;
        _isApplyingProviderTemplate = true;
        try
        {
            SelectComboBoxTag(ProviderKindBox, ProviderManagerRouteItem.KindKeyForRoute(route));
            SelectComboBoxTag(ProviderWireApiBox, route.PreferredWireApi ?? DefaultWireApiForKind(ReadProviderKind()));
        }
        finally
        {
            _isApplyingProviderTemplate = false;
        }

        ProviderNameBox.Text = route.Name;
        ProviderBaseUrlBox.Text = route.UpstreamUrl;
        ProviderApiKeyBox.Password = route.ApiKeyProtected ?? string.Empty;
        ProviderModelsBox.Text = route.ModelFilter ?? string.Empty;
        ProviderPrefixBox.Text = route.Prefix ?? string.Empty;
        ProviderPriorityBox.Value = Math.Clamp(route.Priority, 0, 100);
        ProviderEnabledSwitch.IsOn = route.Enabled;
        ProviderFormTitle.Text = $"正在编辑：{route.Name}";
        ProviderFormSubtitle.Text = "修改后会立即更新透明代理路由。";
        ProviderSaveText.Text = "保存修改";
        ProviderFormStatus.Text = "API Key 留空时会保留原有 Key。";
    }

    private void ResetProviderFields()
    {
        _editingRoute = null;
        ProviderNameBox.Text = string.Empty;
        ProviderBaseUrlBox.Text = string.Empty;
        ProviderApiKeyBox.Password = string.Empty;
        ProviderModelsBox.Text = string.Empty;
        ProviderPrefixBox.Text = string.Empty;
        ProviderPriorityBox.Value = 90;
        ProviderEnabledSwitch.IsOn = true;
    }

    private void ApplyProviderKindTemplate(bool force)
    {
        if (_isApplyingProviderTemplate ||
            ProviderKindBox is null ||
            ProviderWireApiBox is null ||
            ProviderNameBox is null ||
            ProviderBaseUrlBox is null ||
            ProviderModelsBox is null)
        {
            return;
        }

        var defaults = ProviderDefaults(ReadProviderKind());
        SelectComboBoxTag(ProviderWireApiBox, defaults.WireApi);

        if (force || string.IsNullOrWhiteSpace(ProviderNameBox.Text))
        {
            ProviderNameBox.Text = defaults.Name;
        }

        if (force || string.IsNullOrWhiteSpace(ProviderBaseUrlBox.Text))
        {
            ProviderBaseUrlBox.Text = defaults.BaseUrl;
        }

        if (force || string.IsNullOrWhiteSpace(ProviderModelsBox.Text))
        {
            ProviderModelsBox.Text = defaults.Models;
        }
    }

    private int ReadPriority()
    {
        var value = ProviderPriorityBox.Value;
        return double.IsNaN(value) ? 90 : Math.Clamp((int)Math.Round(value), 0, 100);
    }

    private string ReadProviderKind()
        => ReadComboBoxTag(ProviderKindBox) ?? "openai-compatible";

    private static ProviderTemplate ProviderDefaults(string kind)
        => kind switch
        {
            "claude-api" => new ProviderTemplate(
                "Claude API",
                "https://api.anthropic.com",
                "claude-sonnet-4-5",
                ProxyWireApiProbeService.AnthropicMessagesWireApi),
            "codex-api" => new ProviderTemplate(
                "Codex API",
                "https://api.openai.com/v1",
                "gpt-5",
                ProxyWireApiProbeService.ResponsesWireApi),
            _ => new ProviderTemplate(
                "OpenAI 兼容提供商",
                "https://api.openai.com/v1",
                "gpt-4.1",
                ProxyWireApiProbeService.ChatCompletionsWireApi)
        };

    private static string DefaultWireApiForKind(string kind)
        => ProviderDefaults(kind).WireApi;

    private static string? ReadComboBoxTag(ComboBox box)
    {
        if (box.SelectedItem is ComboBoxItem item)
        {
            var tag = item.Tag?.ToString()?.Trim();
            return string.IsNullOrWhiteSpace(tag) ? null : tag;
        }

        return null;
    }

    private static void SelectComboBoxTag(ComboBox box, string? tag)
    {
        var normalized = tag?.Trim() ?? string.Empty;
        foreach (var item in box.Items.OfType<ComboBoxItem>())
        {
            var candidate = item.Tag?.ToString() ?? string.Empty;
            if (string.Equals(candidate, normalized, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedItem = item;
                return;
            }
        }

        box.SelectedIndex = 0;
    }

    private static string? NormalizeDelimitedText(string? value)
    {
        var normalized = string.Join(
            ",",
            (value ?? string.Empty)
                .Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(static item => item.Trim())
                .Where(static item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool TryResolveOAuthAccount(object sender, out TransparentProxyProviderAccount account)
    {
        account = null!;
        if (sender is FrameworkElement { DataContext: ProviderManagerOAuthAccountItem item } &&
            item.Source is TransparentProxyProviderAccount source)
        {
            account = source;
            return true;
        }

        return false;
    }

    private sealed record ProviderTemplate(string Name, string BaseUrl, string Models, string WireApi);
}

public sealed class ProviderManagerOAuthAccountItem
{
    public ProviderManagerOAuthAccountItem(TransparentProxyProviderAccount account)
    {
        Source = account;
        Name = account.Name;
        DetailDisplay = account.DetailDisplay;
        BindingDisplay = account.BindingDisplay;
        StatusText = account.StatusText;
        HealthDisplay = account.HealthDisplay;
    }

    public object Source { get; set; }

    public string Name { get; set; }

    public string DetailDisplay { get; set; }

    public string BindingDisplay { get; set; }

    public string StatusText { get; set; }

    public string HealthDisplay { get; set; }
}

public sealed class ProviderManagerRouteItem
{
    public ProviderManagerRouteItem(RouteDefinition route)
    {
        Route = route;
        Name = string.IsNullOrWhiteSpace(route.Name) ? "未命名提供商" : route.Name.Trim();
        Endpoint = string.IsNullOrWhiteSpace(route.UpstreamUrl) ? "-" : route.UpstreamUrl.Trim();
        KindName = KindNameForRoute(route);
        ProtocolName = ProtocolNameForWireApi(route.PreferredWireApi);
        StatusText = route.Enabled ? "启用" : "停用";
        ModelsText = string.IsNullOrWhiteSpace(route.ModelFilter) ? "未配置模型" : route.ModelFilter.Trim();
        ApiKeyText = string.IsNullOrWhiteSpace(route.ApiKeyProtected) ? "未配置" : "已配置";
        PriorityText = route.Priority.ToString();
        PrefixText = string.IsNullOrWhiteSpace(route.Prefix) ? "无入口前缀" : route.Prefix.Trim();
        CanEditAsApiKeyRoute = !string.Equals(route.AuthMode, TransparentProxyRouteAuthModes.CodexOAuth, StringComparison.OrdinalIgnoreCase);
        ToolTip = $"{Name}\n{Endpoint}\n{KindName} · {ProtocolName}\n模型：{ModelsText}";
    }

    public RouteDefinition Route { get; }

    public string Name { get; }

    public string Endpoint { get; }

    public string KindName { get; }

    public string ProtocolName { get; }

    public string StatusText { get; }

    public string ModelsText { get; }

    public string ApiKeyText { get; }

    public string PriorityText { get; }

    public string PrefixText { get; }

    public bool CanEditAsApiKeyRoute { get; }

    public string ToolTip { get; }

    public static string KindKeyForRoute(RouteDefinition route)
    {
        if (string.Equals(route.AuthMode, TransparentProxyRouteAuthModes.CodexOAuth, StringComparison.OrdinalIgnoreCase))
        {
            return "codex-api";
        }

        var normalized = ProxyWireApiProbeService.NormalizeWireApi(route.PreferredWireApi);
        return normalized switch
        {
            ProxyWireApiProbeService.AnthropicMessagesWireApi => "claude-api",
            ProxyWireApiProbeService.ResponsesWireApi => "codex-api",
            _ => "openai-compatible"
        };
    }

    private static string KindNameForRoute(RouteDefinition route)
    {
        if (string.Equals(route.AuthMode, TransparentProxyRouteAuthModes.CodexOAuth, StringComparison.OrdinalIgnoreCase))
        {
            return "OpenAI/Codex OAuth";
        }

        return KindKeyForRoute(route) switch
        {
            "claude-api" => "Claude API",
            "codex-api" => "Codex API",
            _ => "OpenAI 兼容"
        };
    }

    private static string ProtocolNameForWireApi(string? wireApi)
        => ProxyWireApiProbeService.NormalizeWireApi(wireApi) switch
        {
            ProxyWireApiProbeService.ResponsesWireApi => "Responses",
            ProxyWireApiProbeService.AnthropicMessagesWireApi => "Anthropic Messages",
            _ => "Chat Completions"
        };
}
