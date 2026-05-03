namespace RelayBench.App.Infrastructure;

public sealed class AppStateSnapshot
{
    public string StunTransportKey { get; set; } = "udp";

    public string StunServer { get; set; } = "stun.cloudflare.com";

    public string ProxyBaseUrl { get; set; } = string.Empty;

    public string ProxyApiKey { get; set; } = string.Empty;

    public string ProxyModel { get; set; } = string.Empty;

    public bool ProxyModelWasExplicitlySet { get; set; }

    public string ApplicationCenterBaseUrl { get; set; } = string.Empty;

    public string ApplicationCenterApiKey { get; set; } = string.Empty;

    public string ApplicationCenterModel { get; set; } = string.Empty;

    public string ProxyTimeoutSecondsText { get; set; } = "20";

    public bool ProxyIgnoreTlsErrors { get; set; }

    public string ProxySeriesRoundsText { get; set; } = "5";

    public string ProxySeriesDelayMsText { get; set; } = "1200";

    public string SingleStationModeKey { get; set; } = "quick";

    public string NetworkReviewIssueKey { get; set; } = "interface-unavailable";

    public string ProxyDiagnosticPresetKey { get; set; } = "deep";

    public bool ProxyEnableLongStreamingTest { get; set; }

    public bool ProxyEnableProtocolCompatibilityTest { get; set; } = true;

    public bool ProxyEnableErrorTransparencyTest { get; set; } = true;

    public bool ProxyEnableStreamingIntegrityTest { get; set; } = true;

    public bool ProxyEnableMultiModalTest { get; set; } = true;

    public bool ProxyEnableCacheMechanismTest { get; set; } = true;

    public bool ProxyEnableInstructionFollowingTest { get; set; } = true;

    public bool ProxyEnableDataExtractionTest { get; set; } = true;

    public bool ProxyEnableStructuredOutputEdgeTest { get; set; } = true;

    public bool ProxyEnableToolCallDeepTest { get; set; } = true;

    public bool ProxyEnableReasonMathConsistencyTest { get; set; }

    public bool ProxyEnableCodeBlockDisciplineTest { get; set; } = true;

    public bool ProxyEnableSemanticStabilitySampling { get; set; }

    public bool ProxyEnableCacheIsolationTest { get; set; }

    public string ProxyCacheIsolationAlternateApiKey { get; set; } = string.Empty;

    public bool ProxyEnableOfficialReferenceIntegrityTest { get; set; }

    public string ProxyOfficialReferenceBaseUrl { get; set; } = string.Empty;

    public string ProxyOfficialReferenceApiKey { get; set; } = string.Empty;

    public string ProxyOfficialReferenceModel { get; set; } = string.Empty;

    public string ProxyEmbeddingsModel { get; set; } = string.Empty;

    public string ProxyImagesModel { get; set; } = string.Empty;

    public string ProxyAudioTranscriptionModel { get; set; } = string.Empty;

    public string ProxyAudioSpeechModel { get; set; } = string.Empty;

    public string ProxyModerationModel { get; set; } = string.Empty;

    public List<string> ProxyMultiModelBenchmarkModels { get; set; } = [];

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

    public string TransparentProxyPortText { get; set; } = "17880";

    public string TransparentProxyRoutesText { get; set; } = string.Empty;

    public string TransparentProxyRateLimitPerMinuteText { get; set; } = "60";

    public string TransparentProxyMaxConcurrencyText { get; set; } = "8";

    public bool TransparentProxyEnableFallback { get; set; } = true;

    public bool TransparentProxyEnableCache { get; set; } = true;

    public string TransparentProxyCacheTtlSecondsText { get; set; } = "60";

    public bool TransparentProxyRewriteModel { get; set; } = true;

    public List<RunHistoryEntry> HistoryEntries { get; set; } = [];

    public List<ProxyEndpointHistoryEntrySnapshot> ProxyEndpointHistoryEntries { get; set; } = [];

    public ProxyBatchRankingStateSnapshot ProxyBatchRankingState { get; set; } = new();
}

public sealed class ProxyEndpointHistoryEntrySnapshot
{
    public string BaseUrl { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    public DateTimeOffset FirstSeenAt { get; set; } = DateTimeOffset.Now;

    public DateTimeOffset LastUsedAt { get; set; } = DateTimeOffset.Now;

    public int UseCount { get; set; } = 1;
}

public sealed class ProxyBatchRankingStateSnapshot
{
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.MinValue;

    public string Summary { get; set; } = string.Empty;

    public string Detail { get; set; } = string.Empty;

    public string ChartStatusSummary { get; set; } = string.Empty;

    public List<ProxyBatchRankingRowSnapshot> Rows { get; set; } = [];
}

public sealed class ProxyBatchRankingRowSnapshot
{
    public bool IsSelected { get; set; }

    public int Rank { get; set; }

    public string EntryName { get; set; } = string.Empty;

    public string BaseUrl { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    public string QuickVerdict { get; set; } = string.Empty;

    public string QuickMetrics { get; set; } = string.Empty;

    public string CapabilitySummary { get; set; } = string.Empty;

    public string DeepStatus { get; set; } = string.Empty;

    public string DeepSummary { get; set; } = string.Empty;

    public string DeepCheckedAt { get; set; } = string.Empty;

    public double CompositeScore { get; set; }

    public double StabilityRatio { get; set; }

    public double? TtftMs { get; set; }

    public double? ChatLatencyMs { get; set; }

    public double? TokensPerSecond { get; set; }

    public string Verdict { get; set; } = string.Empty;

    public string SecondaryText { get; set; } = string.Empty;

    public int RunCount { get; set; }
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
