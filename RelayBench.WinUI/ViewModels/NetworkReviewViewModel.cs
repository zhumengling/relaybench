using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelayBench.Core.Models;
using RelayBench.Core.Services;
using RelayBench.WinUI.Services;
using RelayBench.WinUI.Storage;
using Windows.ApplicationModel.DataTransfer;
using RelayBenchPaths = RelayBench.Services.Infrastructure.RelayBenchPaths;

namespace RelayBench.WinUI.ViewModels;

public sealed partial class NetworkReviewViewModel : ObservableObject
{
    private readonly BasicNetworkDiagnosticsService _networkService = new();
    private readonly CloudflareSpeedTestService _speedTestService = new();
    private readonly WebApiTraceService _apiTraceService = new();
    private readonly StunProbeService _stunService = new();
    private readonly RouteDiagnosticsService _routeService = new();
    private readonly PortScanDiagnosticsService _portScanService = new();
    private readonly SplitRoutingDiagnosticsService _splitRoutingService = new();
    private readonly ExitIpRiskReviewService _ipRiskService = new();
    private readonly UnlockCatalogDiagnosticsService _unlockCatalogService = new();
    private readonly GeoIpLookupService _geoIpService = new(RelayBenchPaths.GeoIpCachePath);
    private readonly RouteMapRenderService _routeMapService = new();

    // --- Existing properties ---
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; } = "Ready";
    [ObservableProperty] public partial string PublicIp { get; set; } = "--";
    [ObservableProperty] public partial string HostName { get; set; } = "--";
    [ObservableProperty] public partial string CloudflareColo { get; set; } = "--";
    [ObservableProperty] public partial string DownloadSpeed { get; set; } = "--";
    [ObservableProperty] public partial string UploadSpeed { get; set; } = "--";
    [ObservableProperty] public partial string Jitter { get; set; } = "--";
    [ObservableProperty] public partial int AdapterCount { get; set; }
    [ObservableProperty] public partial string SnapshotTimeText { get; set; } = "--";
    [ObservableProperty] public partial string LocalAdapterName { get; set; } = "--";
    [ObservableProperty] public partial string LocalIpAddress { get; set; } = "--";
    [ObservableProperty] public partial string LocalMacAddress { get; set; } = "--";
    [ObservableProperty] public partial string LinkSpeedText { get; set; } = "--";
    [ObservableProperty] public partial string DefaultGateway { get; set; } = "--";
    [ObservableProperty] public partial string PreferredDns { get; set; } = "--";
    [ObservableProperty] public partial string PreferredDnsLatency { get; set; } = "--";
    [ObservableProperty] public partial string AlternateDns { get; set; } = "--";
    [ObservableProperty] public partial string AlternateDnsLatency { get; set; } = "--";
    [ObservableProperty] public partial string DnsLookupHost { get; set; } = "--";
    [ObservableProperty] public partial string DnsLookupAddress { get; set; } = "--";
    [ObservableProperty] public partial string DnsLeakStatus { get; set; } = "--";
    [ObservableProperty] public partial string ProxyModeText { get; set; } = "--";
    [ObservableProperty] public partial string ProxyRuntimeText { get; set; } = "--";
    [ObservableProperty] public partial string ProxyListenAddress { get; set; } = "--";
    [ObservableProperty] public partial string ProxyConnectionText { get; set; } = "--";
    [ObservableProperty] public partial string ProxyRuleCountText { get; set; } = "--";
    [ObservableProperty] public partial string ProxyModelPoolText { get; set; } = "--";
    [ObservableProperty] public partial string ProxyCacheHitRateText { get; set; } = "--";
    [ObservableProperty] public partial string ProxyTokenSpeedText { get; set; } = "--";
    [ObservableProperty] public partial string ProxyCodexOAuthText { get; set; } = "--";
    [ObservableProperty] public partial string ProxyManagementText { get; set; } = "--";
    [ObservableProperty] public partial string ProxyProtocolSummaryText { get; set; } = "--";
    [ObservableProperty] public partial string ProxyRecentErrorText { get; set; } = "--";
    [ObservableProperty] public partial string PublicIpCountry { get; set; } = "--";
    [ObservableProperty] public partial string PublicIpAsn { get; set; } = "--";
    [ObservableProperty] public partial string PublicIpOrganization { get; set; } = "--";
    [ObservableProperty] public partial string PublicIpType { get; set; } = "--";
    [ObservableProperty] public partial string PublicIpDns { get; set; } = "--";
    [ObservableProperty] public partial string PublicIpFirstSeen { get; set; } = "--";
    [ObservableProperty] public partial string SpeedTestLocation { get; set; } = "--";
    [ObservableProperty] public partial string SpeedPeakDownload { get; set; } = "--";
    [ObservableProperty] public partial string SpeedPeakUpload { get; set; } = "--";
    [ObservableProperty] public partial string SpeedJitterMin { get; set; } = "--";
    [ObservableProperty] public partial string SpeedJitterMax { get; set; } = "--";
    [ObservableProperty] public partial string RouteTotalHops { get; set; } = "--";
    [ObservableProperty] public partial string RouteAverageLatency { get; set; } = "--";
    [ObservableProperty] public partial string RouteLossRate { get; set; } = "--";
    [ObservableProperty] public partial string RouteStatusText { get; set; } = "--";
    [ObservableProperty] public partial string RouteProtocol { get; set; } = "--";
    [ObservableProperty] public partial string RouteCheckedAtText { get; set; } = "--";
    [ObservableProperty] public partial string RouteTraceRawOutput { get; set; } = "";
    [ObservableProperty] public partial string IpRiskScore { get; set; } = "--";
    [ObservableProperty] public partial string IpRiskScoreLabel { get; set; } = "--";
    [ObservableProperty] public partial string RiskMaliciousBehavior { get; set; } = "--";
    [ObservableProperty] public partial string RiskAbuseComplaint { get; set; } = "--";
    [ObservableProperty] public partial string RiskProxyDetected { get; set; } = "--";
    [ObservableProperty] public partial string RiskDatacenter { get; set; } = "--";
    [ObservableProperty] public partial string LastRiskReviewText { get; set; } = "--";
    [ObservableProperty] public partial string LastNatCheckText { get; set; } = "--";

    // --- Phase 14: API 追踪 ---
    [ObservableProperty] public partial bool IsApiTraceRunning { get; set; }
    [ObservableProperty] public partial string ApiTraceError { get; set; } = string.Empty;
    [ObservableProperty] public partial string ApiTracePublicIp { get; set; } = "--";
    [ObservableProperty] public partial string ApiTraceLocationCode { get; set; } = "--";
    [ObservableProperty] public partial string ApiTraceLocationName { get; set; } = "--";
    [ObservableProperty] public partial string ApiTraceColo { get; set; } = "--";
    [ObservableProperty] public partial bool ApiTraceIsSupportedRegion { get; set; }
    [ObservableProperty] public partial string ApiTraceSupportSummary { get; set; } = "--";
    [ObservableProperty] public partial string ApiTraceRawTrace { get; set; } = string.Empty;
    [ObservableProperty] public partial bool HasApiTraceResult { get; set; }
    [ObservableProperty] public partial bool IsOfficialApiTraceDialogOpen { get; set; }
    [ObservableProperty] public partial string OfficialApiTraceDialogTitle { get; set; } = "原始追踪";
    [ObservableProperty] public partial string OfficialApiTraceDialogContent { get; set; } = "Run an API trace or unlock catalog check first.";

    // --- Phase 14: STUN ---
    [ObservableProperty] public partial bool IsStunRunning { get; set; }
    [ObservableProperty] public partial string StunError { get; set; } = string.Empty;
    [ObservableProperty] public partial string StunServerHost { get; set; } = "stun.cloudflare.com";
    [ObservableProperty] public partial bool StunUseTcp { get; set; }
    [ObservableProperty] public partial string StunMappedAddress { get; set; } = "--";
    [ObservableProperty] public partial string StunNatType { get; set; } = "--";
    [ObservableProperty] public partial string StunNatTypeSummary { get; set; } = "--";
    [ObservableProperty] public partial string StunLocalEndpoint { get; set; } = "--";
    [ObservableProperty] public partial string StunRoundTrip { get; set; } = "--";
    [ObservableProperty] public partial string StunClassificationConfidence { get; set; } = "--";
    [ObservableProperty] public partial string StunCoverageSummary { get; set; } = "--";
    [ObservableProperty] public partial string StunReviewRecommendation { get; set; } = "--";
    [ObservableProperty] public partial bool HasStunResult { get; set; }
    public ObservableCollection<KeyValuePair<string, string>> StunAttributes { get; } = [];

    // --- STUN Presets ---
    public IReadOnlyList<StunPreset> StunPresets => StunPresetCatalog.Presets;

    private StunPreset? _selectedStunPreset;
    public StunPreset? SelectedStunPreset
    {
        get => _selectedStunPreset;
        set
        {
            if (SetProperty(ref _selectedStunPreset, value) && value is not null)
            {
                StunServerHost = value.Address;
            }
        }
    }

    // --- Phase 15: Route Trace ---
    [ObservableProperty] public partial bool IsRouteTraceRunning { get; set; }
    [ObservableProperty] public partial string RouteTraceError { get; set; } = string.Empty;
    [ObservableProperty] public partial string RouteTraceTarget { get; set; } = "chatgpt.com";
    [ObservableProperty] public partial string RouteTraceSummary { get; set; } = "--";
    [ObservableProperty] public partial string RouteTraceEngine { get; set; } = "--";
    [ObservableProperty] public partial bool HasRouteTraceResult { get; set; }
    public ObservableCollection<RouteHopResult> RouteTraceHops { get; } = [];
    public ObservableCollection<NetworkReviewRouteNode> RoutePathNodes { get; } = [];
    [ObservableProperty] public partial bool IsRouteMapRendering { get; set; }
    [ObservableProperty] public partial bool HasRouteMapImage { get; set; }
    [ObservableProperty] public partial string RouteMapImagePath { get; set; } = string.Empty;
    [ObservableProperty] public partial string RouteMapSummary { get; set; } = "\u7b49\u5f85\u8def\u7531\u8ffd\u8e2a\u540e\u751f\u6210\u5730\u7406\u8def\u5f84\u56fe";
    [ObservableProperty] public partial string RouteMapGeoSummary { get; set; } = string.Empty;

    // --- Phase 15: MTR Continuous Mode ---
    [ObservableProperty] public partial bool IsMtrRunning { get; set; }
    [ObservableProperty] public partial string MtrStatusText { get; set; } = "--";
    [ObservableProperty] public partial int MtrRoundsCompleted { get; set; }
    private CancellationTokenSource? _mtrCts;
    public ObservableCollection<MtrHopStatistic> MtrHopStatistics { get; } = [];

    // --- Phase 16: Port Scan ---
    [ObservableProperty] public partial bool IsPortScanRunning { get; set; }
    [ObservableProperty] public partial string PortScanError { get; set; } = string.Empty;
    [ObservableProperty] public partial string PortScanTarget { get; set; } = "chatgpt.com";
    [ObservableProperty] public partial string PortScanCustomPorts { get; set; } = string.Empty;
    [ObservableProperty] public partial int PortScanSelectedProfileIndex { get; set; }
    [ObservableProperty] public partial string PortScanSummary { get; set; } = "--";
    [ObservableProperty] public partial string PortScanRawOutput { get; set; } = "";
    [ObservableProperty] public partial string PortScanResolvedAddressText { get; set; } = "--";
    [ObservableProperty] public partial string PortScanProfileText { get; set; } = "--";
    [ObservableProperty] public partial bool HasPortScanResult { get; set; }
    public ObservableCollection<PortScanFinding> PortScanFindings { get; } = [];
    public ObservableCollection<PortScanProfile> PortScanProfiles { get; } = [];

    // --- Phase 16: Port Scan Batch & Export ---
    [ObservableProperty] public partial string PortScanBatchTargets { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsPortScanBatchRunning { get; set; }
    [ObservableProperty] public partial string PortScanBatchSummary { get; set; } = "--";
    [ObservableProperty] public partial string PortScanExportStatus { get; set; } = string.Empty;
    public ObservableCollection<PortScanFinding> PortScanBatchFindings { get; } = [];

    // --- Phase 17: Split Routing ---
    [ObservableProperty] public partial bool IsSplitRoutingRunning { get; set; }
    [ObservableProperty] public partial string SplitRoutingError { get; set; } = string.Empty;
    [ObservableProperty] public partial string SplitRoutingSummary { get; set; } = "--";
    [ObservableProperty] public partial bool SplitRoutingMultiExit { get; set; }
    [ObservableProperty] public partial bool SplitRoutingDnsSplit { get; set; }
    [ObservableProperty] public partial bool HasSplitRoutingResult { get; set; }
    public ObservableCollection<SplitRoutingExitCheck> SplitRoutingExitChecks { get; } = [];
    public ObservableCollection<SplitRoutingDnsView> SplitRoutingDnsViews { get; } = [];

    // --- Phase 17: IP Risk ---
    [ObservableProperty] public partial bool IsIpRiskRunning { get; set; }
    [ObservableProperty] public partial string IpRiskError { get; set; } = string.Empty;
    [ObservableProperty] public partial string IpRiskTargetAddress { get; set; } = string.Empty;
    [ObservableProperty] public partial string IpRiskPublicIp { get; set; } = "--";
    [ObservableProperty] public partial string IpRiskVerdict { get; set; } = "--";
    [ObservableProperty] public partial string IpRiskSummary { get; set; } = "--";
    [ObservableProperty] public partial string IpRiskCountry { get; set; } = "--";
    [ObservableProperty] public partial string IpRiskOrganization { get; set; } = "--";
    [ObservableProperty] public partial bool HasIpRiskResult { get; set; }
    public ObservableCollection<ExitIpRiskSourceResult> IpRiskSources { get; } = [];
    public ObservableCollection<string> IpRiskSignals { get; } = [];
    public ObservableCollection<string> IpRiskPositiveSignals { get; } = [];
    public ObservableCollection<NetworkReviewUnlockRow> UnlockCapabilityRows { get; } = [];
    public ObservableCollection<NetworkReviewRecentCheck> RecentCheckRows { get; } = [];

    // --- Computed visibility helpers ---
    public bool HasApiTraceError => !string.IsNullOrEmpty(ApiTraceError);
    public bool ShowApiTracePlaceholder => !HasApiTraceResult && !IsApiTraceRunning;

    public bool HasSplitRoutingError => !string.IsNullOrEmpty(SplitRoutingError);
    public bool ShowSplitRoutingPlaceholder => !HasSplitRoutingResult && !IsSplitRoutingRunning;
    public string SplitRoutingMultiExitText => SplitRoutingMultiExit ? "\u591A\u51FA\u53E3: \u7591\u4F3C" : "\u591A\u51FA\u53E3: \u6B63\u5E38";
    public string SplitRoutingDnsSplitText => SplitRoutingDnsSplit ? "DNS \u5206\u6D41: \u7591\u4F3C" : "DNS \u5206\u6D41: \u6B63\u5E38";

    public bool HasRouteTraceError => !string.IsNullOrEmpty(RouteTraceError);
    public bool ShowRouteTracePlaceholder => !HasRouteTraceResult && !IsRouteTraceRunning;
    public bool ShowRouteMapPlaceholder => !HasRouteMapImage;

    public bool HasStunError => !string.IsNullOrEmpty(StunError);
    public bool ShowStunPlaceholder => !HasStunResult && !IsStunRunning;
    public string StunTransportLabel => StunUseTcp ? "TCP" : "UDP";

    public bool HasPortScanError => !string.IsNullOrEmpty(PortScanError);
    public bool ShowPortScanPlaceholder => !HasPortScanResult && !IsPortScanRunning;

    public bool HasIpRiskError => !string.IsNullOrEmpty(IpRiskError);
    public bool ShowIpRiskPlaceholder => !HasIpRiskResult && !IsIpRiskRunning;
    public bool IsAnyReviewRunning => IsBusy || IsApiTraceRunning || IsStunRunning || IsRouteTraceRunning || IsPortScanRunning || IsPortScanBatchRunning || IsMtrRunning || IsSplitRoutingRunning || IsIpRiskRunning;
    public string RuntimeStateText => IsAnyReviewRunning ? "\u8FD0\u884C\u4E2D" : "\u5DF2\u5C31\u7EEA";

    // Notify computed properties when underlying properties change
    partial void OnIsBusyChanged(bool value) => NotifyReviewRunningChanged();
    partial void OnApiTraceErrorChanged(string value) => OnPropertyChanged(nameof(HasApiTraceError));
    partial void OnHasApiTraceResultChanged(bool value) => OnPropertyChanged(nameof(ShowApiTracePlaceholder));
    partial void OnIsApiTraceRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowApiTracePlaceholder));
        NotifyReviewRunningChanged();
    }

    partial void OnSplitRoutingErrorChanged(string value) => OnPropertyChanged(nameof(HasSplitRoutingError));
    partial void OnHasSplitRoutingResultChanged(bool value) => OnPropertyChanged(nameof(ShowSplitRoutingPlaceholder));
    partial void OnIsSplitRoutingRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowSplitRoutingPlaceholder));
        NotifyReviewRunningChanged();
    }
    partial void OnSplitRoutingMultiExitChanged(bool value) => OnPropertyChanged(nameof(SplitRoutingMultiExitText));
    partial void OnSplitRoutingDnsSplitChanged(bool value) => OnPropertyChanged(nameof(SplitRoutingDnsSplitText));

    partial void OnRouteTraceErrorChanged(string value) => OnPropertyChanged(nameof(HasRouteTraceError));
    partial void OnHasRouteTraceResultChanged(bool value) => OnPropertyChanged(nameof(ShowRouteTracePlaceholder));
    partial void OnHasRouteMapImageChanged(bool value) => OnPropertyChanged(nameof(ShowRouteMapPlaceholder));
    partial void OnIsRouteTraceRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowRouteTracePlaceholder));
        NotifyReviewRunningChanged();
    }

    partial void OnStunErrorChanged(string value) => OnPropertyChanged(nameof(HasStunError));
    partial void OnStunUseTcpChanged(bool value) => OnPropertyChanged(nameof(StunTransportLabel));
    partial void OnHasStunResultChanged(bool value) => OnPropertyChanged(nameof(ShowStunPlaceholder));
    partial void OnIsStunRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowStunPlaceholder));
        NotifyReviewRunningChanged();
    }

    partial void OnPortScanErrorChanged(string value) => OnPropertyChanged(nameof(HasPortScanError));
    partial void OnHasPortScanResultChanged(bool value) => OnPropertyChanged(nameof(ShowPortScanPlaceholder));
    partial void OnIsPortScanRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowPortScanPlaceholder));
        NotifyReviewRunningChanged();
    }

    partial void OnIpRiskErrorChanged(string value) => OnPropertyChanged(nameof(HasIpRiskError));
    partial void OnHasIpRiskResultChanged(bool value) => OnPropertyChanged(nameof(ShowIpRiskPlaceholder));
    partial void OnIsIpRiskRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowIpRiskPlaceholder));
        NotifyReviewRunningChanged();
    }

    public NetworkReviewViewModel()
    {
        var profiles = _portScanService.GetProfiles();
        foreach (var p in profiles)
        {
            PortScanProfiles.Add(p);
        }

        // Do not seed preview data on startup; all values default to "--" / 0
        // until the user actually runs a diagnostic.
    }

    public void ApplyTransparentProxyStatus(TransparentProxyViewModel proxy)
    {
        var enabledRoutes = proxy.Routes.Count(static route => route.Enabled);
        var providerCount = proxy.ProviderAccounts.Count;
        var modelCount = CountTransparentProxyModels(proxy);
        ProxyModeText = proxy.IsRunning
            ? "透明代理运行中"
            : enabledRoutes > 0 || providerCount > 0 || modelCount > 0
                ? "透明代理已配置"
                : "透明代理未配置";
        ProxyRuntimeText = proxy.IsRunning ? "运行中" : enabledRoutes > 0 || providerCount > 0 || modelCount > 0 ? "已停止" : "未配置";
        ProxyListenAddress = proxy.LocalEndpoint;
        ProxyConnectionText = $"{proxy.ActiveConnections} 活跃连接 · {proxy.TokenSpeed}";
        ProxyRuleCountText = $"{enabledRoutes} 条路由 · {providerCount} 个提供方";
        ProxyModelPoolText = $"{modelCount} 个模型 · {proxy.ModelPool.Count} 个模型池";
        ProxyCacheHitRateText = $"{proxy.CacheHitRate} · {proxy.ResponseCacheSummary}";
        ProxyTokenSpeedText = $"{proxy.TokenSpeed} · {proxy.IoTokens}";
        ProxyCodexOAuthText = BuildCodexOAuthSummary(proxy);
        ProxyManagementText = proxy.ManagementSecuritySummary;
        ProxyProtocolSummaryText = BuildProxyProtocolSummary(proxy);
        ProxyRecentErrorText = BuildProxyRecentErrorSummary(proxy);
    }

    // ========== Existing Commands ==========

}
