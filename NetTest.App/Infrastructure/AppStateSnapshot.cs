namespace NetTest.App.Infrastructure;

public sealed class AppStateSnapshot
{
    public string StunTransportKey { get; set; } = "udp";

    public string StunServer { get; set; } = "stun.cloudflare.com";

    public string ProxyBaseUrl { get; set; } = string.Empty;

    public string ProxyApiKey { get; set; } = string.Empty;

    public string ProxyModel { get; set; } = string.Empty;

    public bool ProxyModelWasExplicitlySet { get; set; }

    public string ProxyTimeoutSecondsText { get; set; } = "20";

    public bool ProxyIgnoreTlsErrors { get; set; }

    public string ProxySeriesRoundsText { get; set; } = "5";

    public string ProxySeriesDelayMsText { get; set; } = "1200";

    public string SingleStationModeKey { get; set; } = "quick";

    public string NetworkReviewIssueKey { get; set; } = "relay-unavailable";

    public string ProxyDiagnosticPresetKey { get; set; } = "deep";

    public bool ProxyEnableLongStreamingTest { get; set; }

    public bool ProxyEnableProtocolCompatibilityTest { get; set; } = true;

    public bool ProxyEnableErrorTransparencyTest { get; set; } = true;

    public bool ProxyEnableStreamingIntegrityTest { get; set; } = true;

    public bool ProxyEnableMultiModalTest { get; set; } = true;

    public bool ProxyEnableCacheMechanismTest { get; set; } = true;

    public bool ProxyEnableCacheIsolationTest { get; set; }

    public string ProxyCacheIsolationAlternateApiKey { get; set; } = string.Empty;

    public bool ProxyEnableOfficialReferenceIntegrityTest { get; set; }

    public string ProxyOfficialReferenceBaseUrl { get; set; } = string.Empty;

    public string ProxyOfficialReferenceApiKey { get; set; } = string.Empty;

    public string ProxyOfficialReferenceModel { get; set; } = string.Empty;

    public bool ProxyBatchEnableLongStreamingTest { get; set; }

    public string ProxyLongStreamSegmentsText { get; set; } = "72";

    public string ProxyBatchTargetsText { get; set; } = string.Empty;

    public List<ProxyBatchConfigItemSnapshot> ProxyBatchItems { get; set; } = [];

    public ProxyBatchDraftSnapshot ProxyBatchDraft { get; set; } = new();

    public string RouteTarget { get; set; } = "chatgpt.com";

    public string RouteResolverKey { get; set; } = "auto";

    public string RouteMaxHopsText { get; set; } = "20";

    public string RouteTimeoutMsText { get; set; } = "900";

    public string RouteSamplesPerHopText { get; set; } = "3";

    public string RouteContinuousDurationSecondsText { get; set; } = "60";

    public string RouteContinuousIntervalMsText { get; set; } = "500";

    public string PortScanTarget { get; set; } = string.Empty;

    public string PortScanProfileKey { get; set; } = "relay-baseline";

    public string PortScanCustomPortsText { get; set; } = string.Empty;

    public string PortScanBatchTargetsText { get; set; } = string.Empty;

    public string PortScanBatchConcurrencyText { get; set; } = "3";

    public string SpeedTestProfileKey { get; set; } = "balanced";

    public string SplitRoutingHostsText { get; set; } = "chatgpt.com\napi.openai.com\ngithub.com\ncloudflare.com\nspeed.cloudflare.com";

    public List<RunHistoryEntry> HistoryEntries { get; set; } = [];
}

public sealed class ProxyBatchConfigItemSnapshot
{
    public string EntryName { get; set; } = string.Empty;

    public string BaseUrl { get; set; } = string.Empty;

    public string EntryApiKey { get; set; } = string.Empty;

    public string EntryModel { get; set; } = string.Empty;

    public string SiteGroupName { get; set; } = string.Empty;

    public string SiteGroupApiKey { get; set; } = string.Empty;

    public string SiteGroupModel { get; set; } = string.Empty;

    public bool IncludeInBatchTest { get; set; } = true;
}

public sealed class ProxyBatchDraftSnapshot
{
    public int EditorModeIndex { get; set; } = 0;

    public string SiteGroupName { get; set; } = string.Empty;

    public string SiteGroupApiKey { get; set; } = string.Empty;

    public string SiteGroupModel { get; set; } = string.Empty;

    public string EntryName { get; set; } = string.Empty;

    public string BaseUrl { get; set; } = string.Empty;

    public string EntryApiKey { get; set; } = string.Empty;

    public string EntryModel { get; set; } = string.Empty;
}
