using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using Microsoft.UI.Xaml;
using RelayBench.Core.Models;
using RelayBench.Services.Infrastructure;
using RelayBench.Services;
using RelayBench.Core.Services;
using RelayBench.WinUI.Charts;
using RelayBench.WinUI.Storage;
using Windows.ApplicationModel.DataTransfer;

namespace RelayBench.WinUI.ViewModels;

public sealed partial class TransparentProxyViewModel : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    private readonly TransparentProxyService _proxyService = new();
    private readonly TransparentProxyLogStore _logStore =
        new(System.IO.Path.Combine(RelayBenchPaths.DataDirectory, "transparent-proxy-logs.sqlite"));
    private readonly TransparentProxyAppDetectorService _appDetector = new();
    private readonly TransparentProxyCodexConfigService _codexConfigService = new();
    private readonly TransparentProxyClaudeConfigService _claudeConfigService = new();
    private readonly TransparentProxyVsCodeSettingsService _vsCodeSettingsService = new();
    private readonly ClientAppConfigApplyService _clientAppConfigApplyService = new();
    private readonly ClientApiConfigRestoreService _clientApiConfigRestoreService = new();
    private readonly TransparentProxyLaunchWrapperService _launchWrapperService = new();
    private readonly TransparentProxyCliEnvironmentService _cliEnvironmentService = new();
    private readonly CodexOAuthService _codexOAuthService = new();
    private readonly ProxySelfTestService _selfTestService = new();
    private readonly SemaphoreSlim _proxyHistoryWriteLock = new(1, 1);
    private CodexOAuthLoginSession? _activeLoginSession;
    private Timer? _proxyHistoryTimer;
    private long _lastProxyHistoryTotalRequests = -1;
    private long _lastProxyHistoryTotalTokens = -1;
    private long _latestTokenMeterTotalOutputTokens;
    private double _latestTokenMeterTokensPerSecond;
    private DateTimeOffset? _latestTokenMeterActivityAt;
    private string _latestTokenMeterSource = string.Empty;
    private bool _latestTokenMeterIsRunning;
    private bool _isRefreshingProviderAccounts;
    private IRouteRepository? _routeRepository;
    private IStrategyRepository _strategyRepository = new StrategyRepository();
    private readonly Dictionary<string, TransparentProxyRoute> _discoveredRoutesById = new(StringComparer.OrdinalIgnoreCase);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunSelfTestCommand))]
    public partial bool IsRunning { get; set; }
    [ObservableProperty] public partial string ListenAddress { get; set; } = "0.0.0.0:8080";
    [ObservableProperty] public partial long TotalRequests { get; set; }
    [ObservableProperty] public partial string SuccessRate { get; set; } = "0.0%";
    [ObservableProperty] public partial string CacheHitRate { get; set; } = "0.0%";
    [ObservableProperty] public partial string TotalTokens { get; set; } = "0";
    [ObservableProperty] public partial string CachedTokens { get; set; } = "0";
    [ObservableProperty] public partial string IoTokens { get; set; } = "0 / 0";
    [ObservableProperty] public partial int ActiveRoutes { get; set; }
    [ObservableProperty] public partial int ActiveConnections { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; } = "已就绪：0.0.0.0:8080";
    [ObservableProperty] public partial string TokenSpeed { get; set; } = "0 tok/s";
    [ObservableProperty] public partial string TokenMeterPrimaryText { get; set; } = "0 tokens";
    [ObservableProperty] public partial string TokenMeterSecondaryText { get; set; } = "等待代理";
    [ObservableProperty] public partial string TokenMeterModeText { get; set; } = "等待";
    [ObservableProperty] public partial string TokenMeterMetricsSummary { get; set; } = "透明代理未运行。";
    [ObservableProperty] public partial TokenMeterVisualTone TokenMeterTone { get; set; } = TokenMeterVisualTone.Wait;
    public Visibility TokenMeterLiveToneVisibility => TokenMeterTone == TokenMeterVisualTone.Live ? Visibility.Visible : Visibility.Collapsed;
    public Visibility TokenMeterIdleToneVisibility => TokenMeterTone == TokenMeterVisualTone.Idle ? Visibility.Visible : Visibility.Collapsed;
    public Visibility TokenMeterWaitToneVisibility => TokenMeterTone == TokenMeterVisualTone.Wait ? Visibility.Visible : Visibility.Collapsed;

    // Percentage change indicators stay at zero until historical deltas are available.
    [ObservableProperty] public partial string TotalRequestsChange { get; set; } = "0";
    [ObservableProperty] public partial string SuccessRateChange { get; set; } = "0.0%";
    [ObservableProperty] public partial string CacheHitRateChange { get; set; } = "0.0%";
    [ObservableProperty] public partial string TotalTokensChange { get; set; } = "0";
    [ObservableProperty] public partial string CachedTokensChange { get; set; } = "0";
    [ObservableProperty] public partial string IoTokensChange { get; set; } = "0 / 0";
    [ObservableProperty] public partial double CacheHitPercentValue { get; set; }
    [ObservableProperty] public partial string ResponseCacheSummary { get; set; } = "响应 0/0";
    [ObservableProperty] public partial string PromptSessionCacheSummary { get; set; } = "会话 0/0";
    [ObservableProperty] public partial string CachePressureSummary { get; set; } = "占用 0 · 等待 0";

    // Phase 4: Protocol Discovery & Self-Test
    [ObservableProperty] public partial string SelfTestResult { get; set; } = "--";
    [ObservableProperty] public partial string SelfTestLatency { get; set; } = "--";
    [ObservableProperty] public partial bool IsDiscoveringProtocols { get; set; }
    [ObservableProperty] public partial bool IsSelfTesting { get; set; }

    // Phase 5: Routing Strategy & Config
    [ObservableProperty] public partial string SelectedRouteStrategy { get; set; } = TransparentProxyRouteStrategies.Smart;
    [ObservableProperty] public partial int SessionAffinityTtlSeconds { get; set; } = 300;
    [ObservableProperty] public partial int ModelCooldownSeconds { get; set; } = 60;
    [ObservableProperty] public partial int RateLimitPerMinute { get; set; } = 600;
    [ObservableProperty] public partial int MaxConcurrency { get; set; } = 32;
    [ObservableProperty] public partial bool EnableFallback { get; set; } = true;
    [ObservableProperty] public partial bool EnableResponseCache { get; set; } = true;
    [ObservableProperty] public partial int RequestRetry { get; set; } = 1;
    [ObservableProperty] public partial int MaxRetryIntervalSeconds { get; set; } = 8;
    [ObservableProperty] public partial int CacheTtlSeconds { get; set; } = 600;
    [ObservableProperty] public partial int UpstreamTimeoutSeconds { get; set; } = 60;
    [ObservableProperty] public partial bool IgnoreTlsErrors { get; set; }
    [ObservableProperty] public partial bool AllowRemoteManagement { get; set; }
    [ObservableProperty] public partial string ManagementSecret { get; set; } = string.Empty;
    [ObservableProperty] public partial string ManagementSecuritySummary { get; set; } = "仅本机管理";

    // Phase 7: Enhanced Metrics
    [ObservableProperty] public partial int FailedRequests { get; set; }
    [ObservableProperty] public partial int FallbackRequests { get; set; }
    [ObservableProperty] public partial int RateLimitedRequests { get; set; }
    [ObservableProperty] public partial string P50Latency { get; set; } = "0 ms";
    [ObservableProperty] public partial string P95Latency { get; set; } = "0 ms";
    [ObservableProperty] public partial int CacheEntryCount { get; set; }
    [ObservableProperty] public partial int ResponseCacheEntryCount { get; set; }
    [ObservableProperty] public partial long ResponseCacheHits { get; set; }
    [ObservableProperty] public partial long ResponseCacheMisses { get; set; }

    // Phase 22: Codex OAuth Login
    [ObservableProperty] public partial bool IsOAuthLoggedIn { get; set; }
    [ObservableProperty] public partial string OAuthUserEmail { get; set; } = string.Empty;
    [ObservableProperty] public partial string OAuthStatusText { get; set; } = "未登录";
    [ObservableProperty] public partial string ManualCallbackUrl { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsOAuthLoggingIn { get; set; }
    [ObservableProperty] public partial bool IsOAuthNotLoggedIn { get; set; } = true;

    // Phase 6: Application Capture
    [ObservableProperty] public partial string AppCaptureStatusText { get; set; } = string.Empty;
    [ObservableProperty] public partial string LaunchWrapperStatusText { get; set; } = string.Empty;
    [ObservableProperty] public partial string LaunchWrapperPathText { get; set; } = string.Empty;
    [ObservableProperty] public partial string LaunchWrapperPreviewText { get; set; } = "等待启动器预览";
    [ObservableProperty] public partial string CliEnvPowerShellText { get; set; } = string.Empty;
    [ObservableProperty] public partial string CliEnvCmdText { get; set; } = string.Empty;
    [ObservableProperty] public partial string ModelPoolTitle { get; set; } = "模型池（可用 0 个）";
    [ObservableProperty] public partial string ProviderAccountSummary { get; set; } = "OpenAI 账号 0 · 可用 0 · API Key 路由 0";
    [ObservableProperty] public partial string PolicyOverviewTitle { get; set; } = "策略与故障切换（0/0 路由）";
    [ObservableProperty] public partial TransparentProxyDashboardSnapshot DashboardSnapshot { get; set; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    private TransparentProxyMetricsSnapshot? _lastMetricsSnapshot;

    /// <summary>
    /// Model pool entries showing health, protocol, and performance metrics per model.
    /// </summary>
    public ObservableCollection<ModelPoolEntry> ModelPool { get; } = new();

    /// <summary>
    /// Route queue entries sorted by priority descending, showing pending requests per route.
    /// </summary>
    public ObservableCollection<RouteQueueEntry> RouteQueue { get; } = new();

    public ObservableCollection<TransparentProxyActivityEvent> RecentActivityEvents { get; } = new();

    public ObservableCollection<TransparentProxyProviderAccount> ProviderAccounts { get; } = new();

    public IReadOnlyList<TransparentProxyPolicyStatusItem> PolicyStatusItems { get; private set; } =
        Array.Empty<TransparentProxyPolicyStatusItem>();

    public bool HasRouteTopology => RouteQueue.Count > 0;
    public bool HasModelPoolTopology => ModelPool.Count > 0;
    public Visibility RouteTopologyEmptyVisibility => HasRouteTopology ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ModelPoolTopologyEmptyVisibility => HasModelPoolTopology ? Visibility.Collapsed : Visibility.Visible;
    public Visibility RecentActivityEmptyVisibility => RecentActivityEvents.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RecentActivityListVisibility => RecentActivityEvents.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ProviderAccountsEmptyVisibility => ProviderAccounts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility TransparentProxySettingsDrawerVisibility => IsTransparentProxySettingsDrawerOpen ? Visibility.Visible : Visibility.Collapsed;
    public Visibility TransparentProxyListenSettingsVisibility => IsTransparentProxySettingsDrawerOpen && IsTransparentProxyListenSettingsOpen ? Visibility.Visible : Visibility.Collapsed;
    public Visibility TransparentProxyProviderSettingsVisibility => IsTransparentProxySettingsDrawerOpen && IsTransparentProxyProviderSettingsOpen ? Visibility.Visible : Visibility.Collapsed;
    public Visibility TransparentProxyOAuthPanelVisibility => IsTransparentProxySettingsDrawerOpen && IsTransparentProxyOAuthPanelOpen ? Visibility.Visible : Visibility.Collapsed;
    public Visibility TransparentProxyAppCaptureSettingsVisibility => IsTransparentProxyAppCaptureSettingsOpen ? Visibility.Visible : Visibility.Collapsed;
    public Visibility TransparentProxyLogExpandedVisibility => IsTransparentProxyLogExpanded ? Visibility.Visible : Visibility.Collapsed;
    public int ListenPort => ParseListenPort();
    public string LocalEndpoint => $"http://127.0.0.1:{ListenPort}";

    [ObservableProperty] public partial bool IsTransparentProxySettingsDrawerOpen { get; set; }
    [ObservableProperty] public partial bool IsTransparentProxyListenSettingsOpen { get; set; }
    [ObservableProperty] public partial bool IsTransparentProxyAppCaptureSettingsOpen { get; set; }
    [ObservableProperty] public partial bool IsTransparentProxyProviderSettingsOpen { get; set; }
    [ObservableProperty] public partial bool IsTransparentProxyOAuthPanelOpen { get; set; }
    [ObservableProperty] public partial bool IsTransparentProxyLogExpanded { get; set; } = true;
    [ObservableProperty] public partial RouteDefinition? TransparentProxyRuntimeRouteSettingsRoute { get; set; }

    /// <summary>
    /// Persisted route definitions displayed in the route list for add/remove/reorder.
    /// </summary>
    public ObservableCollection<RouteDefinition> Routes { get; } = new();

    /// <summary>
    /// Persisted route strategy profiles used to derive runtime route priority.
    /// </summary>
    public ObservableCollection<Strategy> Strategies { get; } = new();

    /// <summary>
    /// Phase 5: Model rewrite rules (source model -> target model).
    /// </summary>
    public ObservableCollection<ModelRewriteRule> ModelRewriteRules { get; } = new();

    /// <summary>
    /// Phase 6: Detected applications for capture configuration.
    /// </summary>
    public ObservableCollection<DetectedAppInfo> DetectedApps { get; } = new();

    /// <summary>
    /// Available route strategy options for the ComboBox.
    /// </summary>
    public List<string> RouteStrategyOptions { get; } =
    [
        TransparentProxyRouteStrategies.Smart,
        TransparentProxyRouteStrategies.RoundRobin,
        TransparentProxyRouteStrategies.Priority,
        TransparentProxyRouteStrategies.LowestLatency,
        TransparentProxyRouteStrategies.SessionAffinity,
        TransparentProxyRouteStrategies.FillFirst
    ];

    public IReadOnlyList<RouteStrategyOption> RouteStrategyDisplayOptions => RouteStrategyOption.Defaults;


    /// <summary>
    /// Log viewer sub-ViewModel for filtering, detail, export, and clear.
    /// </summary>
    public ProxyLogViewerViewModel LogViewer { get; }

    /// <summary>
    /// Sets the route repository for persistence operations.
    /// Called from the page code-behind after construction.
    /// </summary>
    public void SetRouteRepository(IRouteRepository repository)
    {
        _routeRepository = repository;
    }

    /// <summary>
    /// Sets the strategy repository for persistence operations.
    /// Called from the page code-behind after construction.
    /// </summary>
    public void SetStrategyRepository(IStrategyRepository repository)
    {
        _strategyRepository = repository;
    }

    public TransparentProxyViewModel()
    {
        LogViewer = new ProxyLogViewerViewModel(_logStore);
        _proxyService.MetricsChanged += OnMetricsChanged;
        _proxyService.LogEmitted += OnLogEmitted;
        _codexOAuthService.CredentialsChanged += OnCodexOAuthCredentialsChanged;
        RouteQueue.CollectionChanged += (_, _) => RefreshTopologyState();
        ModelPool.CollectionChanged += (_, _) => RefreshTopologyState();
        RecentActivityEvents.CollectionChanged += (_, _) => RefreshRecentActivityState();
        Routes.CollectionChanged += (_, _) => RefreshPolicyStatus();
        Strategies.CollectionChanged += (_, _) => RefreshPolicyStatus();
        DetectedApps.CollectionChanged += (_, _) => RefreshPolicyStatus();
        ModelRewriteRules.CollectionChanged += OnModelRewriteRulesChanged;
        ProviderAccounts.CollectionChanged += (_, _) =>
        {
            if (_isRefreshingProviderAccounts)
            {
                return;
            }

            OnPropertyChanged(nameof(ProviderAccountsEmptyVisibility));
            UpdateProviderAccountSummary();
        };
        RebuildTrendCharts();
        RefreshRouteQueueFromDefinitions();
    }

    private void OnModelRewriteRulesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (ModelRewriteRule rule in e.OldItems)
            {
                rule.PropertyChanged -= OnModelRewriteRulePropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (ModelRewriteRule rule in e.NewItems)
            {
                rule.PropertyChanged += OnModelRewriteRulePropertyChanged;
            }
        }

        RefreshPolicyStatus();
    }

    private void OnModelRewriteRulePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        RefreshPolicyStatus();
    }

    private void RefreshTopologyState()
    {
        OnPropertyChanged(nameof(HasRouteTopology));
        OnPropertyChanged(nameof(HasModelPoolTopology));
        OnPropertyChanged(nameof(RouteTopologyEmptyVisibility));
        OnPropertyChanged(nameof(ModelPoolTopologyEmptyVisibility));
    }

    private void RefreshRecentActivityState()
    {
        OnPropertyChanged(nameof(RecentActivityEmptyVisibility));
        OnPropertyChanged(nameof(RecentActivityListVisibility));
    }

    private void OnLogEmitted(object? sender, TransparentProxyLogEntry entry)
    {
        // Persist to SQLite
        _ = _logStore.AppendAsync(entry);
        // Append to the log viewer in real-time
        LogViewer.AppendEntry(entry);
        AppendRecentActivity(entry);
    }

    private void AppendRecentActivity(TransparentProxyLogEntry entry)
    {
        if (!IsActivityEvent(entry))
        {
            return;
        }

        var source = string.IsNullOrWhiteSpace(entry.RouteName) || entry.RouteName == "-"
            ? "本地代理"
            : entry.RouteName;
        var status = entry.StatusCode > 0 ? entry.StatusCode.ToString(CultureInfo.InvariantCulture) : "--";
        var detail = string.IsNullOrWhiteSpace(entry.Message)
            ? entry.Path
            : entry.Message;

        RecentActivityEvents.Insert(0, new TransparentProxyActivityEvent(
            entry.Timestamp.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            entry.Level,
            source,
            detail,
            status,
            string.IsNullOrWhiteSpace(entry.RequestId) ? "--" : entry.RequestId));

        while (RecentActivityEvents.Count > 12)
        {
            RecentActivityEvents.RemoveAt(RecentActivityEvents.Count - 1);
        }
    }

    private static bool IsActivityEvent(TransparentProxyLogEntry entry)
        => entry.StatusCode >= 400 ||
           string.Equals(entry.Level, "WARN", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(entry.Level, "ERROR", StringComparison.OrdinalIgnoreCase);

    public void RefreshRecentActivityFromLogEntries(IEnumerable<ProxyLogDisplayEntry> entries)
    {
        RecentActivityEvents.Clear();
        foreach (var entry in entries
                     .Where(static item => item.StatusCode >= 400 ||
                                           string.Equals(item.Level, "WARN", StringComparison.OrdinalIgnoreCase) ||
                                           string.Equals(item.Level, "ERROR", StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(static item => item.Timestamp)
                     .Take(12))
        {
            RecentActivityEvents.Add(new TransparentProxyActivityEvent(
                entry.Timestamp.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                entry.Level,
                string.IsNullOrWhiteSpace(entry.RouteName) || entry.RouteName == "-" ? "本地代理" : entry.RouteName,
                string.IsNullOrWhiteSpace(entry.Message) ? entry.Path : entry.Message,
                entry.StatusBadge,
                string.IsNullOrWhiteSpace(entry.RequestId) ? "--" : entry.RequestId));
        }
    }

    private void OpenTransparentProxySettingsSection(bool listen, bool provider, bool oauth)
    {
        IsTransparentProxySettingsDrawerOpen = true;
        IsTransparentProxyListenSettingsOpen = listen;
        IsTransparentProxyProviderSettingsOpen = provider;
        IsTransparentProxyOAuthPanelOpen = oauth;
    }

    partial void OnIsTransparentProxySettingsDrawerOpenChanged(bool value)
        => NotifyTransparentProxySettingsVisibilityChanged();

    partial void OnIsTransparentProxyListenSettingsOpenChanged(bool value)
        => NotifyTransparentProxySettingsVisibilityChanged();

    partial void OnIsTransparentProxyProviderSettingsOpenChanged(bool value)
        => NotifyTransparentProxySettingsVisibilityChanged();

    partial void OnIsTransparentProxyOAuthPanelOpenChanged(bool value)
        => NotifyTransparentProxySettingsVisibilityChanged();

    partial void OnIsTransparentProxyAppCaptureSettingsOpenChanged(bool value)
        => OnPropertyChanged(nameof(TransparentProxyAppCaptureSettingsVisibility));

    partial void OnIsTransparentProxyLogExpandedChanged(bool value)
        => OnPropertyChanged(nameof(TransparentProxyLogExpandedVisibility));

    partial void OnTokenMeterToneChanged(TokenMeterVisualTone value)
    {
        OnPropertyChanged(nameof(TokenMeterLiveToneVisibility));
        OnPropertyChanged(nameof(TokenMeterIdleToneVisibility));
        OnPropertyChanged(nameof(TokenMeterWaitToneVisibility));
    }

    private void NotifyTransparentProxySettingsVisibilityChanged()
    {
        OnPropertyChanged(nameof(TransparentProxySettingsDrawerVisibility));
        OnPropertyChanged(nameof(TransparentProxyListenSettingsVisibility));
        OnPropertyChanged(nameof(TransparentProxyProviderSettingsVisibility));
        OnPropertyChanged(nameof(TransparentProxyOAuthPanelVisibility));
    }

    partial void OnListenAddressChanged(string value)
    {
        OnPropertyChanged(nameof(ListenPort));
        OnPropertyChanged(nameof(LocalEndpoint));
        RefreshPolicyStatus();
    }

    partial void OnSelectedRouteStrategyChanged(string value)
    {
        RefreshPolicyStatus();
    }

    partial void OnSessionAffinityTtlSecondsChanged(int value)
    {
        RefreshPolicyStatus();
    }

    partial void OnModelCooldownSecondsChanged(int value)
    {
        RefreshPolicyStatus();
        RefreshRouteQueueFromDefinitions();
    }

    partial void OnRequestRetryChanged(int value)
    {
        RefreshPolicyStatus();
        RefreshRouteQueueFromDefinitions();
    }

    partial void OnMaxRetryIntervalSecondsChanged(int value)
    {
        RefreshPolicyStatus();
        RefreshRouteQueueFromDefinitions();
    }

    partial void OnAllowRemoteManagementChanged(bool value)
    {
        RefreshManagementSecuritySummary();
        RefreshPolicyStatus();
    }

    partial void OnManagementSecretChanged(string value)
    {
        RefreshManagementSecuritySummary();
        RefreshPolicyStatus();
    }

    private int ParseListenPort()
    {
        var text = ListenAddress.Trim();
        var colon = text.LastIndexOf(':');
        var portText = colon >= 0 ? text[(colon + 1)..] : text;
        return int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) &&
               port is >= 1 and <= 65535
            ? port
            : 8080;
    }

    private static string FormatProtocol(string? wireApi)
        => wireApi switch
        {
            ProxyWireApiProbeService.ResponsesWireApi => "Responses",
            ProxyWireApiProbeService.ChatCompletionsWireApi => "OpenAI Chat",
            "anthropic-messages" => "Anthropic",
            _ => "未知"
        };

    private static string BuildClientApplyRelayRouteName(string sourceName, string model)
    {
        var normalizedSource = string.IsNullOrWhiteSpace(sourceName)
            ? "Application Access"
            : sourceName.Trim();
        var normalizedModel = string.IsNullOrWhiteSpace(model)
            ? "default"
            : model.Trim();
        return $"Application Access Claude relay {normalizedSource} {normalizedModel}";
    }

    private static bool RouteContainsModel(RouteDefinition route, string model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return false;
        }

        return SplitCsv(route.ModelFilter)
            .Select(static item => ParseInlineModelMapping(item))
            .Any(mapping =>
                string.Equals(mapping.Name, model, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mapping.Alias, model, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeEndpointForClientApplyRoute(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
        {
            return normalized.TrimEnd('/');
        }

        var builder = new UriBuilder(uri)
        {
            Query = string.Empty,
            Fragment = string.Empty
        };
        return builder.Uri.ToString().TrimEnd('/');
    }

    private static IReadOnlyList<string> SplitCsv(string? value)
        => (value ?? string.Empty)
            .Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private IReadOnlyList<RouteDefinition> BuildTransparentProxyCandidateRouteDefinitions()
    {
        List<RouteDefinition> candidates = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        void AddCandidate(string? name, string? baseUrl, string? apiKey, string? model, string source)
        {
            var normalizedUrl = BatchEndpointText.NormalizeBaseUrl(baseUrl);
            if (normalizedUrl is null || IsLocalTransparentProxyEndpoint(normalizedUrl))
            {
                return;
            }

            var normalizedModel = string.IsNullOrWhiteSpace(model) ? string.Empty : model.Trim();
            var key = $"{NormalizeEndpointForClientApplyRoute(normalizedUrl)}|{normalizedModel}";
            if (!seen.Add(key))
            {
                return;
            }

            var routeName = FirstNonEmpty(name, BatchEndpointText.TryGetHost(normalizedUrl), source) ?? $"Route {candidates.Count + 1}";
            candidates.Add(new RouteDefinition(
                Id: TransparentProxyRouteTextCodec.BuildRouteId(routeName, normalizedUrl, normalizedModel),
                Name: routeName,
                UpstreamUrl: normalizedUrl,
                ApiKeyProtected: string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim(),
                Priority: 100,
                ModelFilter: string.IsNullOrWhiteSpace(normalizedModel) ? null : normalizedModel,
                Enabled: true,
                UpdatedAtUtc: DateTime.UtcNow,
                AuthMode: TransparentProxyRouteAuthModes.ApiKey));
        }

        try
        {
            var shared = SharedEndpointStore.Load();
            if (shared is not null)
            {
                AddCandidate("当前共享入口", shared.BaseUrl, shared.ApiKey, shared.Model, "当前入口");
            }
        }
        catch
        {
            // Candidate generation remains useful even when the shared endpoint file is unreadable.
        }

        try
        {
            var batchEditor = new BatchSiteEditorViewModel();
            foreach (var site in batchEditor.Sites.Where(static site => site.IsIncluded))
            {
                AddCandidate(site.DisplayName, site.BaseUrl, site.ApiKey, site.Model, "批量入口组");
            }
        }
        catch
        {
            // Ignore malformed batch-site persistence and keep candidates from other sources.
        }

        return candidates;
    }

    private IReadOnlyList<BatchSiteEntry> BuildBatchEntriesFromTransparentProxyRoutes()
    {
        List<BatchSiteEntry> entries = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (var route in Routes)
        {
            if (string.Equals(route.AuthMode, TransparentProxyRouteAuthModes.CodexOAuth, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var normalizedUrl = BatchEndpointText.NormalizeBaseUrl(route.UpstreamUrl);
            if (normalizedUrl is null)
            {
                continue;
            }

            var model = ResolveBatchModelFromTransparentProxyRoute(route);
            var key = $"{NormalizeEndpointForClientApplyRoute(normalizedUrl)}|{model}";
            if (!seen.Add(key))
            {
                continue;
            }

            var models = ResolveBatchModelsFromTransparentProxyRoute(route);
            var entry = new BatchSiteEntry(
                normalizedUrl,
                route.ApiKeyProtected ?? string.Empty,
                model,
                timeout: Math.Clamp(UpstreamTimeoutSeconds, 5, 300),
                tlsIgnore: IgnoreTlsErrors,
                isIncluded: route.Enabled,
                groupName: "透明代理",
                name: FirstNonEmpty(route.Name, BatchEndpointText.TryGetHost(normalizedUrl)) ?? normalizedUrl)
            {
                ModelCatalogSummary = models.Count == 0
                    ? "等待真实模型数据"
                    : $"透明代理路由模型 {models.Count} 个",
                ProtocolSummary = string.IsNullOrWhiteSpace(route.PreferredWireApi)
                    ? "未探测"
                    : FormatProtocol(route.PreferredWireApi)
            };

            foreach (var routeModel in models)
            {
                entry.AvailableModels.Add(routeModel);
            }

            entries.Add(entry);
        }

        return entries;
    }

    private RouteDefinition? FindRouteByUpstreamUrl(string upstreamUrl)
    {
        var normalized = NormalizeEndpointForClientApplyRoute(upstreamUrl);
        return Routes.FirstOrDefault(route =>
            string.Equals(
                NormalizeEndpointForClientApplyRoute(route.UpstreamUrl),
                normalized,
                StringComparison.OrdinalIgnoreCase));
    }

    private bool IsLocalTransparentProxyEndpoint(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var host = uri.Host;
        return uri.Port == ListenPort &&
               (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolveBatchModelFromTransparentProxyRoute(RouteDefinition route)
    {
        var model = SplitCsv(route.ModelFilter)
            .Select(static item => ParseInlineModelMapping(item).Name)
            .FirstOrDefault(static item => !string.IsNullOrWhiteSpace(item));

        return model?.Trim() ?? string.Empty;
    }

    private static IReadOnlyList<string> ResolveBatchModelsFromTransparentProxyRoute(RouteDefinition route)
        => SplitCsv(route.ModelFilter)
            .Select(static item => ParseInlineModelMapping(item).Name)
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static IReadOnlyDictionary<string, string> ParseRouteHeaders(string? headersText)
    {
        if (string.IsNullOrWhiteSpace(headersText))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            using var document = JsonDocument.Parse(headersText);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                var name = property.Name.Trim();
                var value = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString()
                    : property.Value.ToString();
                if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(value))
                {
                    headers[name] = value.Trim();
                }
            }

            return headers;
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string FormatMilliseconds(long milliseconds)
        => milliseconds > 0 ? $"{milliseconds} ms" : "0 ms";

    private static string FormatMilliseconds(double milliseconds)
        => milliseconds > 0 ? $"{milliseconds:F1} ms" : "0 ms";

    private static string FormatTokenSpeed(double value)
        => value > 0 ? $"{value:F1} tok/s" : "0 tok/s";

    private static string FormatPercent(long numerator, long denominator)
        => $"{CalculatePercentValue(numerator, denominator):F1}%";

    private static double CalculatePercentValue(long numerator, long denominator)
        => denominator > 0 ? (double)numerator / denominator * 100 : 0d;

    private static string CalculateCacheHitRateText(TransparentProxyMetricsSnapshot metrics)
        => $"{CalculateCacheHitPercentValue(metrics):F1}%";

    private static double CalculateCacheHitPercentValue(TransparentProxyMetricsSnapshot metrics)
    {
        var cacheHits = Math.Max(0, metrics.ResponseCacheHits + metrics.PromptSessionCacheHits + metrics.CacheHits);
        var cacheMisses = Math.Max(0, metrics.ResponseCacheMisses + metrics.PromptSessionCacheMisses);
        if (cacheHits + cacheMisses > 0)
        {
            return (double)cacheHits / (cacheHits + cacheMisses) * 100;
        }

        return metrics.TotalRequests > 0
            ? (double)Math.Max(0, metrics.CacheHits) / metrics.TotalRequests * 100
            : 0;
    }

    private static string FormatCooldown(DateTimeOffset until)
    {
        var remaining = until - DateTimeOffset.UtcNow;
        return remaining > TimeSpan.Zero ? $"{remaining.TotalSeconds:F0}s" : "0s";
    }

    private static string ResolveRouteCooldown(TransparentProxyRouteMetrics route)
    {
        var routeCooldown = route.CircuitOpenUntil - DateTimeOffset.UtcNow;
        var modelCooldown = route.ModelCooldowns?
            .Select(static item => item.CooldownUntil - DateTimeOffset.UtcNow)
            .DefaultIfEmpty(TimeSpan.Zero)
            .Max() ?? TimeSpan.Zero;
        var remaining = routeCooldown > modelCooldown ? routeCooldown : modelCooldown;
        return remaining > TimeSpan.Zero ? $"{remaining.TotalSeconds:F0}s" : "0s";
    }

    private static double ResolvePoolCooldownSeconds(TransparentProxyModelPoolSnapshot pool)
    {
        var remaining = pool.Members
            .SelectMany(static member => new[]
            {
                member.CircuitOpenUntil - DateTimeOffset.UtcNow,
                member.ModelCooldownUntil - DateTimeOffset.UtcNow
            })
            .Where(static value => value > TimeSpan.Zero)
            .Select(static value => value.TotalSeconds)
            .DefaultIfEmpty(0)
            .Max();
        return remaining;
    }

    private static string BuildRouteLatestError(TransparentProxyRouteMetrics route)
    {
        if (route.LastStatusCode >= 500)
        {
            return $"上游 {route.LastStatusCode}";
        }

        if (route.LastStatusCode == 429)
        {
            return "限流 429";
        }

        if (route.ConsecutiveFailures > 0)
        {
            return $"连续失败 {route.ConsecutiveFailures}";
        }

        return string.Empty;
    }

    private static string BuildModelPoolLatestError(TransparentProxyModelPoolSnapshot pool)
    {
        if (pool.OpenCircuitMembers > 0)
        {
            return $"熔断 {pool.OpenCircuitMembers}";
        }

        if (pool.Failed > 0)
        {
            return $"失败 {pool.Failed}";
        }

        return string.Empty;
    }

    private string BuildRoutePolicyDisplay(RouteDefinition? route)
    {
        if (route is null)
        {
            return BuildGlobalFailoverPolicyDisplay();
        }

        List<string> parts = [];
        if (!string.IsNullOrWhiteSpace(route.OutboundProxy))
        {
            parts.Add(route.OutboundProxy.Equals("direct", StringComparison.OrdinalIgnoreCase) ? "直连" : "出站代理");
        }

        var retry = ResolveEffectiveRequestRetry(route.RequestRetry);
        if (retry > 0)
        {
            parts.Add($"重试 {retry}");
        }

        var maxRetryInterval = ResolveEffectiveMaxRetryInterval(route.MaxRetryIntervalSeconds);
        if (retry > 0)
        {
            parts.Add($"间隔 {maxRetryInterval}s");
        }

        if (route.ModelCooldownSeconds is { } cooldown)
        {
            parts.Add($"冷却 {cooldown}s");
        }

        if (!string.IsNullOrWhiteSpace(route.Prefix))
        {
            parts.Add($"前缀 {route.Prefix}");
        }

        if (!string.IsNullOrWhiteSpace(route.ExcludedModelPatterns))
        {
            parts.Add("排除模型");
        }

        if (route.CodexOAuthFastMode)
        {
            parts.Add("FAST");
        }

        return parts.Count == 0 ? BuildGlobalFailoverPolicyDisplay() : string.Join(" · ", parts);
    }

    private string BuildRoutePolicyDisplay(TransparentProxyRoute route)
    {
        List<string> parts = [];
        if (!string.IsNullOrWhiteSpace(route.OutboundProxy))
        {
            parts.Add(route.OutboundProxy.Equals("direct", StringComparison.OrdinalIgnoreCase) ? "直连" : "出站代理");
        }

        var retry = ResolveEffectiveRequestRetry(route.RequestRetry);
        if (retry > 0)
        {
            parts.Add($"重试 {retry}");
        }

        var maxRetryInterval = ResolveEffectiveMaxRetryInterval(route.MaxRetryIntervalSeconds);
        if (retry > 0)
        {
            parts.Add($"间隔 {maxRetryInterval}s");
        }

        if (route.ModelCooldownSeconds is { } cooldown)
        {
            parts.Add($"冷却 {cooldown}s");
        }

        if (!string.IsNullOrWhiteSpace(route.Prefix))
        {
            parts.Add($"前缀 {route.Prefix}");
        }

        if (route.ExcludedModelPatterns.Count > 0)
        {
            parts.Add("排除模型");
        }

        if (route.CodexOAuthFastMode)
        {
            parts.Add("FAST");
        }

        return parts.Count == 0 ? BuildGlobalFailoverPolicyDisplay() : string.Join(" · ", parts);
    }

    private string BuildGlobalFailoverPolicyDisplay()
    {
        var retry = ResolveEffectiveRequestRetry(null);
        return retry > 0
            ? $"重试 {retry} · 间隔 {ResolveEffectiveMaxRetryInterval(null)}s"
            : "默认";
    }

    private int ResolveEffectiveRequestRetry(int? routeRetry)
        => Math.Clamp(routeRetry ?? RequestRetry, 0, 10);

    private int ResolveEffectiveMaxRetryInterval(int? routeMaxRetryIntervalSeconds)
        => Math.Clamp(routeMaxRetryIntervalSeconds ?? MaxRetryIntervalSeconds, 1, 300);

    private void UpdateModelPoolTitle()
    {
        var healthy = ModelPool.Sum(static item => item.HealthyCount);
        var total = ModelPool.Sum(static item => item.TotalCount);
        ModelPoolTitle = total == 0
            ? "模型池（可用 0 个）"
            : $"模型池（可用 {healthy}/{total}）";
    }

    private void RefreshProviderAccounts()
    {
        _isRefreshingProviderAccounts = true;
        try
        {
            ProviderAccounts.Clear();
            var routes = BuildRuntimeRoutes();
            var credentials = _codexOAuthService.GetCredentials();
            var credentialsById = credentials.ToDictionary(static item => item.Id, StringComparer.OrdinalIgnoreCase);
            HashSet<string> boundCredentialIds = new(StringComparer.OrdinalIgnoreCase);

            foreach (var route in routes)
            {
                if (route.IsCodexOAuth &&
                    credentialsById.TryGetValue(route.OAuthCredentialId, out var credential))
                {
                    boundCredentialIds.Add(credential.Id);
                    ProviderAccounts.Add(BuildCodexOAuthProviderAccount(route, credential, isBoundRoute: true));
                    continue;
                }

                ProviderAccounts.Add(BuildRouteProviderAccount(route));
            }

            foreach (var credential in credentials
                         .Where(credential => !boundCredentialIds.Contains(credential.Id))
                         .OrderBy(static credential => credential.State)
                         .ThenBy(static credential => credential.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                ProviderAccounts.Add(BuildCodexOAuthProviderAccount(null, credential, isBoundRoute: false));
            }
        }
        finally
        {
            _isRefreshingProviderAccounts = false;
        }

        OnPropertyChanged(nameof(ProviderAccountsEmptyVisibility));
        UpdateProviderAccountSummary();
    }

    private TransparentProxyProviderAccount BuildRouteProviderAccount(TransparentProxyRoute route)
    {
        var cooldown = route.CircuitOpenUntil > DateTimeOffset.UtcNow;
        var modelCount = route.Models.Count(static model => !string.IsNullOrWhiteSpace(model));
        var protocol = BuildRouteProtocolSummary(route);
        return new TransparentProxyProviderAccount(
            route.Name,
            InferProvider(route),
            ToHost(route.BaseUrl),
            protocol,
            $"{modelCount} 模型",
            cooldown ? "冷却中" : "可用",
            route.AuthMode.Equals(TransparentProxyRouteAuthModes.CodexOAuth, StringComparison.OrdinalIgnoreCase)
                ? "OpenAI 账号路由"
                : "API Key / 基础 URL",
            BuildRouteProviderDetail(route),
            BuildRouteProviderHealth(route),
            false);
    }

    private static TransparentProxyProviderAccount BuildCodexOAuthProviderAccount(
        TransparentProxyRoute? route,
        CodexOAuthCredential credential,
        bool isBoundRoute)
    {
        var plan = string.IsNullOrWhiteSpace(credential.PlanType) ? "Codex" : credential.PlanType.Trim();
        var name = isBoundRoute && route is not null
            ? route.Name
            : $"OpenAI {plan} {credential.DisplayName}".Trim();
        var endpoint = route is null ? CodexOAuthConstants.DefaultBackendBaseUrl : ToHost(route.BaseUrl);
        var modelCount = route?.Models.Count(static model => !string.IsNullOrWhiteSpace(model)) ?? 0;
        return new TransparentProxyProviderAccount(
            name,
            "OpenAI 账号",
            endpoint,
            route is null ? "Codex OAuth" : BuildRouteProtocolSummary(route),
            modelCount > 0 ? $"{modelCount} 模型" : "凭据",
            BuildCredentialStatusText(credential),
            isBoundRoute ? "已绑定路由" : "未绑定路由",
            BuildCredentialDetailText(credential),
            BuildCredentialHealthText(credential),
            true,
            credential.Id);
    }

    private static string BuildRouteProviderDetail(TransparentProxyRoute route)
    {
        List<string> parts = [];
        parts.Add(string.IsNullOrWhiteSpace(route.OutboundProxy)
            ? "直连"
            : route.OutboundProxy.Equals("direct", StringComparison.OrdinalIgnoreCase) ? "直连" : $"出站 {route.OutboundProxy}");
        parts.Add(string.IsNullOrWhiteSpace(route.Prefix) ? "统一入口" : $"前缀 {route.Prefix}");
        if (route.RequestRetry > 0)
        {
            parts.Add($"重试 {route.RequestRetry}");
        }

        if (route.CodexOAuthFastMode)
        {
            parts.Add("FAST");
        }

        return string.Join(" · ", parts);
    }

    private static string BuildRouteProviderHealth(TransparentProxyRoute route)
    {
        if (route.CircuitOpenUntil > DateTimeOffset.UtcNow)
        {
            return $"冷却 {FormatCooldown(route.CircuitOpenUntil)}";
        }

        return route.ProtocolCheckedAt is null ? "协议未探测" : $"协议 {BuildRouteProtocolSummary(route)}";
    }

    private static string BuildCredentialStatusText(CodexOAuthCredential credential)
    {
        if (credential.QuotaExceeded)
        {
            return "配额冷却";
        }

        return credential.State switch
        {
            CodexOAuthCredentialState.Ready => "可用",
            CodexOAuthCredentialState.Refreshing => "刷新中",
            CodexOAuthCredentialState.RefreshBackoff => "刷新退避",
            CodexOAuthCredentialState.NeedsRelogin => "需重登",
            CodexOAuthCredentialState.Disabled => "已禁用",
            _ => credential.State.ToString()
        };
    }

    private static string BuildCredentialDetailText(CodexOAuthCredential credential)
    {
        List<string> parts = [];
        if (!string.IsNullOrWhiteSpace(credential.PlanType))
        {
            parts.Add($"计划 {credential.PlanType.Trim()}");
        }

        if (credential.AccessTokenExpiresAt is { } expiresAt)
        {
            parts.Add($"过期 {expiresAt.ToLocalTime():MM-dd HH:mm}");
        }

        if (credential.LastRefreshAt is { } lastRefreshAt)
        {
            parts.Add($"刷新 {lastRefreshAt.ToLocalTime():MM-dd HH:mm}");
        }

        if (credential.RefreshBackoffUntil is { } backoffUntil && backoffUntil > DateTimeOffset.UtcNow)
        {
            parts.Add($"退避至 {backoffUntil.ToLocalTime():HH:mm}");
        }

        if (!string.IsNullOrWhiteSpace(credential.LastError))
        {
            parts.Add(credential.LastError.Trim());
        }

        if (credential.QuotaExceeded && !string.IsNullOrWhiteSpace(credential.QuotaReason))
        {
            parts.Add(credential.QuotaReason.Trim());
        }

        return parts.Count == 0 ? "OpenAI 账号凭据已加载" : string.Join(" · ", parts);
    }

    private static string BuildCredentialHealthText(CodexOAuthCredential credential)
    {
        if (credential.QuotaNextRecoverAt is { } recoverAt && recoverAt > DateTimeOffset.UtcNow)
        {
            return $"配额恢复 {recoverAt.ToLocalTime():HH:mm}";
        }

        if (credential.RefreshFailureCount > 0)
        {
            return $"刷新失败 {credential.RefreshFailureCount}";
        }

        return string.IsNullOrWhiteSpace(credential.AccountIdHash)
            ? "账号已托管"
            : $"账号 {credential.AccountIdHash}";
    }

    private void UpdateProviderAccountSummary()
    {
        var oauthCount = ProviderAccounts.Count(static item => item.IsOAuthCredential);
        var routeCount = ProviderAccounts.Count - oauthCount;
        var readyCount = ProviderAccounts.Count(static item => string.Equals(item.StatusText, "可用", StringComparison.Ordinal));
        ProviderAccountSummary = $"OpenAI 账号 {oauthCount} · 可用 {readyCount} · API Key 路由 {routeCount}";
        RefreshPolicyStatus();
    }

    private void RefreshManagementSecuritySummary()
    {
        ManagementSecuritySummary = AllowRemoteManagement
            ? string.IsNullOrWhiteSpace(ManagementSecret)
                ? "远程未启用：需要管理密钥"
                : "远程管理已启用"
            : "仅本机管理";
    }

    private void RefreshPolicyStatus()
    {
        var totalRoutes = Routes.Count;
        var enabledRoutes = Routes.Count(static route => route.Enabled);
        var disabledRoutes = Math.Max(0, totalRoutes - enabledRoutes);
        var configuredRoutes = Routes.Where(static route => route.Enabled).ToArray();
        var outboundProxyRoutes = configuredRoutes.Count(static route =>
            !string.IsNullOrWhiteSpace(route.OutboundProxy) &&
            !route.OutboundProxy.Equals("direct", StringComparison.OrdinalIgnoreCase));
        var directRoutes = configuredRoutes.Length - outboundProxyRoutes;
        var retryRoutes = configuredRoutes.Count(route => ResolveEffectiveRequestRetry(route.RequestRetry) > 0);
        var maxRetry = configuredRoutes
            .Select(route => ResolveEffectiveRequestRetry(route.RequestRetry))
            .DefaultIfEmpty(ResolveEffectiveRequestRetry(null))
            .Max();
        var cooldownRoutes = configuredRoutes.Count(static route => route.ModelCooldownSeconds.GetValueOrDefault() > 0);
        var maxCooldown = configuredRoutes
            .Select(static route => route.ModelCooldownSeconds.GetValueOrDefault())
            .DefaultIfEmpty(ModelCooldownSeconds)
            .Max();
        var rewriteCount = ModelRewriteRules.Count(static rule =>
            !string.IsNullOrWhiteSpace(rule.SourceModel) &&
            !string.IsNullOrWhiteSpace(rule.TargetModel));
        var payloadRuleCount = configuredRoutes.Count(static route => !string.IsNullOrWhiteSpace(route.PayloadRulesText));
        var excludedRouteCount = configuredRoutes.Count(static route => !string.IsNullOrWhiteSpace(route.ExcludedModelPatterns));
        var strategyProfiles = Strategies.Count;
        var strategyTargets = Strategies
            .SelectMany(static strategy => strategy.TargetRouteIds)
            .Where(static routeId => !string.IsNullOrWhiteSpace(routeId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var capturedApps = DetectedApps.Count(static app => app.IsTakeoverEnabled);
        var backupApps = DetectedApps.Count(static app => app.HasLiveBackup);
        var oauthCount = ProviderAccounts.Count(static item => item.IsOAuthCredential);
        var readyProviders = ProviderAccounts.Count(static item => string.Equals(item.StatusText, "可用", StringComparison.Ordinal));
        var routeStrategyName = RouteStrategyOption.GetDisplayName(SelectedRouteStrategy);

        PolicyOverviewTitle = $"策略与故障切换（{enabledRoutes}/{totalRoutes} 路由）";
        PolicyStatusItems = new[]
        {
            new TransparentProxyPolicyStatusItem(
            "路由策略",
            routeStrategyName,
            strategyProfiles == 0
                ? $"按路由优先级执行 · 停用 {disabledRoutes}"
                : $"{strategyProfiles} 个策略档案 · 命中 {strategyTargets} 条路由",
            "策略",
            TransparentProxyPolicyTone.Accent),
            new TransparentProxyPolicyStatusItem(
            "故障切换",
            $"{retryRoutes} 路由",
            $"最大重试 {maxRetry} · 回退 {FallbackRequests} · 失败 {FailedRequests}",
            retryRoutes > 0 ? "已启用" : "未配置",
            retryRoutes > 0 ? TransparentProxyPolicyTone.Healthy : TransparentProxyPolicyTone.Warning),
            new TransparentProxyPolicyStatusItem(
            "冷却/限流",
            $"{cooldownRoutes} 路由",
            $"模型冷却最高 {Math.Max(0, maxCooldown)}s · 限流 {RateLimitedRequests}",
            RateLimitedRequests > 0 ? "有告警" : "正常",
            RateLimitedRequests > 0 ? TransparentProxyPolicyTone.Warning : TransparentProxyPolicyTone.Healthy),
            new TransparentProxyPolicyStatusItem(
            "会话亲和",
            $"{SessionAffinityTtlSeconds}s",
            $"策略 {routeStrategyName} · 本地入口 {LocalEndpoint}",
            SelectedRouteStrategy.Equals(TransparentProxyRouteStrategies.SessionAffinity, StringComparison.OrdinalIgnoreCase)
                ? "路由中"
                : "可用",
            TransparentProxyPolicyTone.Accent),
            new TransparentProxyPolicyStatusItem(
            "出站代理",
            $"{outboundProxyRoutes} 条",
            $"直连 {directRoutes} · 每路由 outbound-proxy 独立生效",
            outboundProxyRoutes > 0 ? "分流" : "直连",
            outboundProxyRoutes > 0 ? TransparentProxyPolicyTone.Accent : TransparentProxyPolicyTone.Healthy),
            new TransparentProxyPolicyStatusItem(
            "模型规则",
            $"{rewriteCount} 重写",
            $"Payload 规则 {payloadRuleCount} · 排除模型 {excludedRouteCount}",
            rewriteCount + payloadRuleCount + excludedRouteCount > 0 ? "已配置" : "默认",
            TransparentProxyPolicyTone.Accent),
            new TransparentProxyPolicyStatusItem(
            "应用接入",
            $"{capturedApps}/{DetectedApps.Count}",
            $"恢复点 {backupApps} · 配置写入使用本地备份",
            capturedApps > 0 ? "接管中" : "待接入",
            capturedApps > 0 ? TransparentProxyPolicyTone.Healthy : TransparentProxyPolicyTone.Warning),
            new TransparentProxyPolicyStatusItem(
            "OpenAI 账号",
            $"{readyProviders}/{ProviderAccounts.Count}",
            $"OAuth {oauthCount} · API Key 路由 {Math.Max(0, ProviderAccounts.Count - oauthCount)}",
            readyProviders > 0 ? "可用" : "0",
            readyProviders > 0 ? TransparentProxyPolicyTone.Healthy : TransparentProxyPolicyTone.Warning),
            new TransparentProxyPolicyStatusItem(
            "管理 API",
            AllowRemoteManagement ? "远程" : "本机",
            ManagementSecuritySummary,
            string.IsNullOrWhiteSpace(ManagementSecret) ? "无密钥" : "有密钥",
            AllowRemoteManagement && string.IsNullOrWhiteSpace(ManagementSecret)
                ? TransparentProxyPolicyTone.Warning
                : TransparentProxyPolicyTone.Healthy)
        };
        OnPropertyChanged(nameof(PolicyStatusItems));
    }

    private void OnCodexOAuthCredentialsChanged(object? sender, EventArgs e)
    {
        ApplyOAuthState(_codexOAuthService.GetCredentials(), startBackgroundRefresh: false);
        RefreshProviderAccounts();
    }

    private static string InferProvider(TransparentProxyRoute route)
    {
        if (route.IsCodexOAuth)
        {
            return "OpenAI 账号";
        }

        var host = ToHost(route.BaseUrl).ToLowerInvariant();
        if (host.Contains("openai", StringComparison.OrdinalIgnoreCase)) return "OpenAI";
        if (host.Contains("anthropic", StringComparison.OrdinalIgnoreCase)) return "Anthropic";
        if (host.Contains("google", StringComparison.OrdinalIgnoreCase) || host.Contains("gemini", StringComparison.OrdinalIgnoreCase)) return "Google";
        if (host.Contains("deepseek", StringComparison.OrdinalIgnoreCase)) return "DeepSeek";
        if (host.Contains("qwen", StringComparison.OrdinalIgnoreCase) || host.Contains("dashscope", StringComparison.OrdinalIgnoreCase)) return "Qwen";
        return "OpenAI Compatible";
    }

    private static string ToHost(string baseUrl)
        => Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host)
            ? uri.Host
            : baseUrl;

    private static string BuildRouteProtocolSummary(TransparentProxyRoute route)
    {
        List<string> parts = [];
        if (route.ResponsesSupported == true) parts.Add("R");
        if (route.AnthropicMessagesSupported == true) parts.Add("A");
        if (route.ChatCompletionsSupported == true) parts.Add("C");
        if (parts.Count == 0)
        {
            parts.Add(FormatProtocol(route.PreferredWireApi));
        }

        return string.Join(" / ", parts);
    }

    private static string BuildRouteProtocolSummary(TransparentProxyRouteMetrics route)
    {
        List<string> parts = [];
        if (route.ResponsesSupported == true) parts.Add("R");
        if (route.AnthropicMessagesSupported == true) parts.Add("A");
        if (route.ChatCompletionsSupported == true) parts.Add("C");
        if (parts.Count == 0)
        {
            parts.Add(FormatProtocol(route.PreferredWireApi));
        }

        return string.Join(" / ", parts);
    }

    private Dictionary<string, int> ResolveRuntimePriorities(IReadOnlyList<TransparentProxyRoute> routes)
    {
        if (Strategies.Count == 0)
        {
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }

        var availableRouteIds = routes
            .Select(static route => route.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> runtimePriorityByRouteId = new(StringComparer.OrdinalIgnoreCase);

        foreach (var strategy in Strategies
                     .Where(static strategy => strategy.TargetRouteIds.Count > 0)
                     .OrderByDescending(static strategy => strategy.Priority)
                     .ThenByDescending(static strategy => strategy.UpdatedAtUtc)
                     .ThenBy(static strategy => strategy.Name, StringComparer.OrdinalIgnoreCase))
        {
            var strategyBase = 1_000_000_000L + Math.Max(0L, strategy.Priority) * 1_000_000L;
            var offset = 0;
            foreach (var routeId in strategy.TargetRouteIds
                         .Select(static id => id?.Trim() ?? string.Empty)
                         .Where(static id => !string.IsNullOrWhiteSpace(id))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!availableRouteIds.Contains(routeId))
                {
                    continue;
                }

                var runtimePriorityLong = strategyBase - offset * 1_000L;
                var runtimePriority = runtimePriorityLong > int.MaxValue
                    ? int.MaxValue
                    : (int)runtimePriorityLong;
                if (!runtimePriorityByRouteId.TryGetValue(routeId, out var existingPriority) ||
                    runtimePriority > existingPriority)
                {
                    runtimePriorityByRouteId[routeId] = runtimePriority;
                }

                offset++;
            }
        }

        return runtimePriorityByRouteId;
    }

    private (string Model, string WireApi) ResolveAppCaptureEndpoint()
    {
        var route = BuildRuntimeRoutes().FirstOrDefault();
        var model = route?.Models.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ??
                    route?.Model ??
                    "gpt-5.4";
        var wireApi = route?.PreferredWireApi ?? ProxyWireApiProbeService.ResponsesWireApi;
        return (model, wireApi);
    }

    private void OnMetricsChanged(object? sender, TransparentProxyMetricsSnapshot metrics)
    {
        _lastMetricsSnapshot = metrics;
        IsRunning = metrics.IsRunning;
        TotalRequests = metrics.TotalRequests;
        FailedRequests = metrics.FailedRequests;
        FallbackRequests = metrics.FallbackRequests;
        RateLimitedRequests = metrics.RateLimitedRequests;
        P50Latency = FormatMilliseconds(metrics.P50LatencyMs);
        P95Latency = FormatMilliseconds(metrics.P95LatencyMs);
        CacheEntryCount = metrics.CacheEntryCount;
        ResponseCacheEntryCount = metrics.ResponseCacheEntryCount;
        ResponseCacheHits = metrics.ResponseCacheHits;
        ResponseCacheMisses = metrics.ResponseCacheMisses;
        SuccessRate = FormatPercent(metrics.SuccessRequests, metrics.TotalRequests);
        CacheHitRate = CalculateCacheHitRateText(metrics);
        CacheHitPercentValue = CalculateCacheHitPercentValue(metrics);
        ResponseCacheSummary = $"响应 {metrics.ResponseCacheHits}/{metrics.ResponseCacheMisses}";
        PromptSessionCacheSummary = $"会话 {metrics.PromptSessionCacheHits}/{metrics.PromptSessionCacheMisses}";
        CachePressureSummary = $"占用 {metrics.ResponseCacheInFlightKeys} · 等待 {metrics.ResponseCacheLeaseWaits}";
        TotalTokens = FormatTokenCount(metrics.TotalInputTokens + metrics.TotalOutputTokens);
        CachedTokens = FormatTokenCount(metrics.PromptCacheTokens);
        IoTokens = $"{FormatTokenCount(metrics.TotalInputTokens)} / {FormatTokenCount(metrics.TotalOutputTokens)}";
        ActiveRoutes = metrics.Routes.Count;
        ActiveConnections = metrics.ActiveRequests;
        TokenSpeed = FormatTokenSpeed(metrics.TokensPerSecond);
        _latestTokenMeterIsRunning = metrics.IsRunning;
        _latestTokenMeterTotalOutputTokens = metrics.TotalOutputTokens;
        _latestTokenMeterTokensPerSecond = metrics.TokensPerSecond;
        _latestTokenMeterActivityAt = metrics.LastTokenActivityAt;
        _latestTokenMeterSource = ResolveLatestTokenMeterSource(metrics.RecentUsageEvents);
        RefreshTokenMeterIdleState();
        TokenMeterMetricsSummary = BuildTokenMeterMetricsSummary(metrics);
        DashboardSnapshot = new TransparentProxyDashboardSnapshot(
            metrics.TotalRequests,
            metrics.SuccessRequests,
            metrics.FailedRequests,
            metrics.FallbackRequests,
            metrics.RateLimitedRequests,
            metrics.CacheHits,
            metrics.TotalInputTokens,
            metrics.TotalOutputTokens,
            metrics.PromptCacheTokens,
            metrics.P50LatencyMs,
            metrics.P95LatencyMs,
            metrics.TokensPerSecond,
            metrics.ActiveRequests,
            metrics.Routes.Count,
            metrics.ModelPools?.Count ?? 0);

        // Update model pool from snapshot
        ModelPool.Clear();
        if (metrics.ModelPools is { Count: > 0 })
        {
            foreach (var pool in metrics.ModelPools)
            {
                var isUnhealthy = pool.OpenCircuitMembers > 0;
                var cooldownRemaining = ResolvePoolCooldownSeconds(pool);
                ModelPool.Add(new ModelPoolEntry(
                    pool.ModelName,
                    pool.HealthyMembers,
                    pool.MemberCount,
                    pool.ProtocolSummary,
                    pool.Sent,
                    pool.BestLatencyMs,
                    isUnhealthy,
                    cooldownRemaining,
                    pool.Failed,
                    pool.OpenCircuitMembers,
                    metrics.RateLimitedRequests.ToString(CultureInfo.InvariantCulture),
                    CacheHitRate,
                    BuildModelPoolLatestError(pool)));
            }
        }

        UpdateModelPoolTitle();

        // Update route queue from route metrics
        if (metrics.Routes.Count > 0)
        {
            RouteQueue.Clear();
            var totalSent = Math.Max(1, metrics.Routes.Sum(static route => route.Sent));
            foreach (var route in metrics.Routes.OrderByDescending(r => r.Sent))
            {
                var configuredRoute = Routes.FirstOrDefault(item => string.Equals(item.Id, route.Id, StringComparison.OrdinalIgnoreCase));
                var circuitState = route.CircuitState switch
                {
                    "Open" => CircuitState.Open,
                    "HalfOpen" => CircuitState.HalfOpen,
                    _ => CircuitState.Closed
                };
                var consecutiveFailures = route.ConsecutiveFailures;
                var successRate = FormatPercent(route.Success, route.Sent);
                var routeTokenSpeed = route.Sent > 0 && metrics.TokensPerSecond > 0
                    ? metrics.TokensPerSecond * route.Sent / totalSent
                    : 0;
                RouteQueue.Add(new RouteQueueEntry(
                    route.Name,
                    Math.Max(0, route.Sent - route.Success - route.Failed),
                    configuredRoute?.Priority ?? 0,
                    route.LastLatencyMs / 1000.0,
                    new CircuitBreakerInfo(circuitState, consecutiveFailures),
                    FormatProtocol(route.PreferredWireApi),
                    FormatMilliseconds(route.LastLatencyMs),
                    FormatTokenSpeed(routeTokenSpeed),
                    successRate,
                    circuitState == CircuitState.Open ? "熔断" : "活跃",
                    route.Sent,
                    route.Success,
                    route.Failed,
                    ResolveRouteCooldown(route),
                    route.LastStatusCode == 429 ? "1+" : "0",
                    CacheHitRate,
                    BuildRoutePolicyDisplay(configuredRoute),
                    BuildRouteLatestError(route),
                    route.Id));
            }
        }
        else
        {
            RouteQueue.Clear();
        }

        RefreshProviderAccounts();

        // Phase 23: Append to ring buffer and rebuild trend charts (throttled)
        AppendToMetricsHistory(metrics);
        RefreshPolicyStatus();
    }

    // Phase 4: Protocol Discovery
    [RelayCommand]
    private async Task DiscoverProtocolsAsync()
    {
        var runtimeRoutes = BuildRuntimeRoutes();
        if (runtimeRoutes.Count == 0)
        {
            StatusText = "请先添加至少一条路由，再进行协议发现";
            return;
        }

        IsDiscoveringProtocols = true;
        StatusText = "正在发现路由协议...";
        try
        {
            var proxyDiagnostics = new ProxyDiagnosticsService();
            var modelCache = new ProxyEndpointModelCacheService();
            var protocolProbe = new ProxyEndpointProtocolProbeService(proxyDiagnostics, modelCache);
            var discoveryService = new TransparentProxyProtocolDiscoveryService(proxyDiagnostics, modelCache, protocolProbe);

            var options = new TransparentProxyProtocolDiscoveryOptions(
                ForceProbe: false,
                FetchCatalogModels: false,
                IgnoreTlsErrors: false,
                UpstreamTimeoutSeconds: 10,
                FallbackModel: runtimeRoutes.SelectMany(static route => route.Models).FirstOrDefault() ?? string.Empty);

            var result = await discoveryService.DiscoverAsync(runtimeRoutes, options);

            foreach (var route in result.HydratedRoutes)
            {
                _discoveredRoutesById[route.Id] = route;
            }

            await PersistDiscoveredRouteProtocolsAsync(result.HydratedRoutes);

            if (IsRunning)
            {
                _proxyService.UpdateRoutes(BuildRuntimeRoutes());
            }

            RefreshRouteQueueFromDefinitions();
            StatusText = $"协议发现完成：{result.Snapshots.Count}/{runtimeRoutes.Count} 条路由，{result.ProbedModels} 次探测";
        }
        catch (Exception ex)
        {
            StatusText = $"协议发现失败: {ex.Message}";
        }
        finally
        {
            IsDiscoveringProtocols = false;
        }
    }

    // Phase 4: Self-Test
    public bool CanRunSelfTest => IsRunning;

    [RelayCommand(CanExecute = nameof(CanRunSelfTest))]
    private async Task RunSelfTestAsync()
    {
        IsSelfTesting = true;
        SelfTestResult = "测试中...";
        SelfTestLatency = "--";
        try
        {
            var port = ParseListenPort();
            var proxyAddress = $"http://127.0.0.1:{port}";
            var result = await _selfTestService.RunAsync(proxyAddress);

            if (result.Success)
            {
                SelfTestResult = "通过";
                SelfTestLatency = $"{result.Latency.TotalMilliseconds:F0} ms";
            }
            else
            {
                SelfTestResult = $"失败: {result.ErrorMessage}";
                SelfTestLatency = "--";
            }
        }
        catch (Exception ex)
        {
            SelfTestResult = $"错误: {ex.Message}";
            SelfTestLatency = "--";
        }
        finally
        {
            IsSelfTesting = false;
        }
    }

    [RelayCommand]
    private async Task TestTransparentProxyHealthAsync()
        => await RunSelfTestAsync();
    [RelayCommand]
    private async Task RefreshTransparentProxyRoutesAsync()
    {
        await LoadRoutesAsync();
        RefreshRouteQueueFromDefinitions();
        StatusText = $"\u5DF2\u5237\u65B0\u900F\u660E\u4EE3\u7406\u8DEF\u7531\uFF1A{Routes.Count} \u6761";
    }

    [RelayCommand]
    private void ToggleTransparentProxySettingsDrawer()
    {
        IsTransparentProxySettingsDrawerOpen = !IsTransparentProxySettingsDrawerOpen;
        if (!IsTransparentProxySettingsDrawerOpen)
        {
            IsTransparentProxyListenSettingsOpen = false;
            IsTransparentProxyProviderSettingsOpen = false;
            IsTransparentProxyOAuthPanelOpen = false;
        }
        else if (!IsTransparentProxyListenSettingsOpen &&
                 !IsTransparentProxyProviderSettingsOpen &&
                 !IsTransparentProxyOAuthPanelOpen)
        {
            IsTransparentProxyListenSettingsOpen = true;
        }

        StatusText = IsTransparentProxySettingsDrawerOpen
            ? "\u5DF2\u5C55\u5F00\u900F\u660E\u4EE3\u7406\u8BBE\u7F6E"
            : "\u5DF2\u6536\u8D77\u900F\u660E\u4EE3\u7406\u8BBE\u7F6E";
    }

    [RelayCommand]
    private void ToggleTransparentProxyListenSettings()
    {
        var shouldOpen = !IsTransparentProxyListenSettingsOpen;
        OpenTransparentProxySettingsSection(listen: shouldOpen, provider: false, oauth: false);
        StatusText = shouldOpen
            ? "\u5DF2\u5C55\u5F00\u900F\u660E\u4EE3\u7406\u76D1\u542C\u8BBE\u7F6E"
            : "\u5DF2\u6536\u8D77\u900F\u660E\u4EE3\u7406\u76D1\u542C\u8BBE\u7F6E";
    }

    [RelayCommand]
    private Task ToggleTransparentProxyAppCaptureSettingsAsync()
    {
        IsTransparentProxyAppCaptureSettingsOpen = !IsTransparentProxyAppCaptureSettingsOpen;
        if (IsTransparentProxyAppCaptureSettingsOpen && DetectedApps.Count == 0)
        {
            DetectApps();
        }

        StatusText = IsTransparentProxyAppCaptureSettingsOpen
            ? "\u5DF2\u5C55\u5F00\u5E94\u7528\u63A5\u7BA1"
            : "\u5DF2\u6536\u8D77\u5E94\u7528\u63A5\u7BA1";
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void ToggleTransparentProxyProviderSettings()
    {
        var shouldOpen = !IsTransparentProxyProviderSettingsOpen;
        OpenTransparentProxySettingsSection(listen: false, provider: shouldOpen, oauth: false);
        if (IsTransparentProxyProviderSettingsOpen)
        {
            RefreshProviderAccounts();
        }
        StatusText = shouldOpen
            ? "\u5DF2\u5C55\u5F00\u63D0\u4F9B\u65B9 / 路由\u8BBE\u7F6E"
            : "\u5DF2\u6536\u8D77\u63D0\u4F9B\u65B9 / 路由\u8BBE\u7F6E";
    }

    [RelayCommand]
    private void ToggleTransparentProxyOAuthPanel()
    {
        var shouldOpen = !IsTransparentProxyOAuthPanelOpen;
        OpenTransparentProxySettingsSection(listen: false, provider: false, oauth: shouldOpen);
        if (IsTransparentProxyOAuthPanelOpen)
        {
            ApplyOAuthState(_codexOAuthService.GetCredentials(), startBackgroundRefresh: false);
            RefreshProviderAccounts();
        }
        StatusText = shouldOpen
            ? "\u5DF2\u5C55\u5F00 OpenAI \u8D26\u53F7\u9762\u677F"
            : "\u5DF2\u6536\u8D77 OpenAI \u8D26\u53F7\u9762\u677F";
    }

    [RelayCommand]
    private void ToggleTransparentProxyLogExpanded()
    {
        IsTransparentProxyLogExpanded = !IsTransparentProxyLogExpanded;
        StatusText = IsTransparentProxyLogExpanded
            ? "\u5DF2\u5C55\u5F00\u900F\u660E\u4EE3\u7406\u65E5\u5FD7"
            : "\u5DF2\u6536\u8D77\u900F\u660E\u4EE3\u7406\u65E5\u5FD7";
    }

    [RelayCommand]
    private void OpenTransparentProxyRuntimeRouteSettings(RouteQueueEntry? route)
    {
        if (route is null)
        {
            TransparentProxyRuntimeRouteSettingsRoute = null;
            StatusText = "\u8BF7\u5148\u9009\u62E9\u8FD0\u884C\u4E2D\u7684\u900F\u660E\u4EE3\u7406\u8DEF\u7531";
            return;
        }

        var configuredRoute = Routes.FirstOrDefault(item =>
                !string.IsNullOrWhiteSpace(route.RouteId) &&
                string.Equals(item.Id, route.RouteId, StringComparison.OrdinalIgnoreCase))
            ?? Routes.FirstOrDefault(item => string.Equals(item.Name, route.RouteName, StringComparison.OrdinalIgnoreCase));
        if (configuredRoute is null)
        {
            TransparentProxyRuntimeRouteSettingsRoute = null;
            StatusText = $"\u672A\u627E\u5230\u53EF\u7F16\u8F91\u7684\u8FD0\u884C\u8DEF\u7531\uFF1A{route.RouteName}";
            return;
        }

        TransparentProxyRuntimeRouteSettingsRoute = configuredRoute;
        StatusText = $"\u5DF2\u5B9A\u4F4D\u8FD0\u884C\u8DEF\u7531\u8BBE\u7F6E\uFF1A{configuredRoute.Name}";
    }

    [RelayCommand]
    private async Task GenerateTransparentProxyCandidateRoutesAsync()
    {
        if (_routeRepository is not null)
        {
            await LoadRoutesAsync();
        }

        var candidates = BuildTransparentProxyCandidateRouteDefinitions();
        if (candidates.Count == 0)
        {
            StatusText = "没有从当前入口或批量入口组找到可生成的透明代理候选路由";
            return;
        }

        var added = 0;
        var updated = 0;
        var basePriority = Math.Max(100, Routes.Select(static route => route.Priority).DefaultIfEmpty(0).Max() + candidates.Count);
        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            var existing = FindRouteByUpstreamUrl(candidate.UpstreamUrl);
            var route = existing is null
                ? candidate with
                {
                    Priority = basePriority - index,
                    UpdatedAtUtc = DateTime.UtcNow
                }
                : existing with
                {
                    Name = candidate.Name,
                    UpstreamUrl = candidate.UpstreamUrl,
                    ApiKeyProtected = candidate.ApiKeyProtected,
                    ModelFilter = candidate.ModelFilter,
                    Enabled = candidate.Enabled,
                    PreferredWireApi = candidate.PreferredWireApi ?? existing.PreferredWireApi,
                    AuthMode = candidate.AuthMode ?? existing.AuthMode,
                    UpdatedAtUtc = DateTime.UtcNow
                };

            await AddOrUpdateRouteAsync(route);
            if (existing is null)
            {
                added++;
            }
            else
            {
                updated++;
            }
        }

        RefreshRouteQueueFromDefinitions();
        StatusText = $"已生成透明代理候选路由：新增 {added} 条，更新 {updated} 条；可继续运行协议发现补齐线路能力";
    }

    [RelayCommand]
    private async Task ExportTransparentProxyRoutesToBatchAsync()
    {
        if (_routeRepository is not null && Routes.Count == 0)
        {
            await LoadRoutesAsync();
        }

        var entries = BuildBatchEntriesFromTransparentProxyRoutes();
        if (entries.Count == 0)
        {
            StatusText = "没有可导出到批量入口组的透明代理 API Key 路由";
            return;
        }

        var editor = new BatchSiteEditorViewModel();
        var (added, updated) = editor.UpsertGeneratedCandidates(entries);
        StatusText = $"已导出透明代理路由到批量入口组：新增 {added} 条，更新 {updated} 条";
    }

    [RelayCommand]
    private async Task ExportTransparentProxyLogsAsync()
    {
        await LogViewer.ExportCsvCommand.ExecuteAsync(null);
        StatusText = string.IsNullOrWhiteSpace(LogViewer.StatusMessage)
            ? "\u900F\u660E\u4EE3\u7406\u65E5\u5FD7\u5DF2\u5BFC\u51FA"
            : LogViewer.StatusMessage;
    }

    [RelayCommand]
    private async Task ClearTransparentProxyLogsAsync()
    {
        await LogViewer.ClearLogsCommand.ExecuteAsync(null);
        StatusText = string.IsNullOrWhiteSpace(LogViewer.StatusMessage)
            ? "\u900F\u660E\u4EE3\u7406\u65E5\u5FD7\u5DF2\u6E05\u9664"
            : LogViewer.StatusMessage;
    }

    [RelayCommand]
    private void CloseTransparentProxyLogDetail()
        => LogViewer.CloseDetailCommand.Execute(null);

    // Phase 5: 添加模型重写规则
    [RelayCommand]
    private void AddModelRewriteRule()
    {
        ModelRewriteRules.Add(new ModelRewriteRule("", ""));
    }

    [RelayCommand]
    private void RemoveModelRewriteRule(ModelRewriteRule? rule)
    {
        if (rule is not null)
            ModelRewriteRules.Remove(rule);
    }

}
