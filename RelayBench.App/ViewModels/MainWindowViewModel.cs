using System.Collections.ObjectModel;
using System.IO;
using RelayBench.App.Infrastructure;
using RelayBench.App.Services;
using RelayBench.Core.Models;
using RelayBench.Core.Services;

namespace RelayBench.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private const string DefaultStunServerHost = StunServerPresetCatalog.RecommendedUdpNatReviewHost;

    private readonly BasicNetworkDiagnosticsService _networkDiagnosticsService = new();
    private readonly WebApiTraceService _webApiTraceService = new();
    private readonly StunProbeService _stunProbeService = new();
    private readonly ProxyDiagnosticsService _proxyDiagnosticsService = new();
    private readonly ProxyEndpointModelCacheService _proxyEndpointModelCacheService =
        new(Path.Combine(RelayBenchPaths.DataDirectory, "endpoint-model-cache.sqlite"));
    private readonly CloudflareSpeedTestService _cloudflareSpeedTestService = new();
    private readonly RouteDiagnosticsService _routeDiagnosticsService = new();
    private readonly PortScanDiagnosticsService _portScanDiagnosticsService = new();
    private readonly SplitRoutingDiagnosticsService _splitRoutingDiagnosticsService = new();
    private readonly TransparentProxyService _transparentProxyService = new();
    private readonly DiagnosticReportService _diagnosticReportService = new();
    private readonly ChatConversationService _chatConversationService = new();
    private readonly ChatAttachmentImportService _chatAttachmentImportService = new();
    private readonly ModelChatExportService _modelChatExportService = new();
    private readonly ChatSessionStore _chatSessionStore = new();
    private ChatSessionsDocument _chatSessionsDocument = new();
    private readonly AppStateStore _appStateStore = new();
    private readonly List<RunHistoryEntry> _historyEntries = [];
    private UnlockCatalogResult? _lastUnlockCatalogResult;
    private StunProbeResult? _lastStunResult;
    private ProxyDiagnosticsResult? _lastProxySingleResult;
    private ProxyStabilityResult? _lastProxyStabilityResult;
    private ProxyBatchPlan? _lastProxyBatchPlan;
    private IReadOnlyList<ProxyBatchProbeRow> _lastProxyBatchRows = Array.Empty<ProxyBatchProbeRow>();
    private readonly List<IReadOnlyList<ProxyBatchProbeRow>> _proxyBatchChartRuns = [];
    private IReadOnlyList<ProxyBatchProbeRow> _currentProxyBatchLiveRows = Array.Empty<ProxyBatchProbeRow>();
    private int _currentProxyBatchLiveTargetCount;
    private readonly List<BatchDeepChartRowState> _batchDeepChartStates = [];
    private IReadOnlyList<ProxyTrendEntry> _lastProxyTrendRecords = Array.Empty<ProxyTrendEntry>();
    private PortScanResult? _lastPortScanResult;
    private CancellationTokenSource? _currentChatCancellationSource;
    private bool _isLoadingChatSession;
    private string _activeChatSessionId = string.Empty;
    private string? _chatEditingMessageId;

    private bool _isBusy;
    private bool _isChatStreaming;
    private string _statusMessage = "准备就绪，随时可以开始诊断。";
    private string _chatInputText = string.Empty;
    private string _chatSystemPrompt = string.Empty;
    private string _chatTemperatureText = "0.7";
    private string _chatMaxTokensText = "2048";
    private string _selectedChatReasoningEffortKey = "auto";
    private string _chatCandidateModel = string.Empty;
    private bool _isChatSettingsPanelOpen;
    private string _chatStatusMessage = "\u586b\u597d\u63a5\u53e3\u548c\u6a21\u578b\u540e\u5373\u53ef\u8fdb\u884c\u771f\u5b9e\u5bf9\u8bdd\u5b9e\u6d4b\u3002";
    private string _chatMetricsSummary = "\u5c1a\u672a\u53d1\u9001\u5bf9\u8bdd\u8bf7\u6c42\u3002";
    private string _chatReasoningEffortSummary = "\u81ea\u52a8\u6a21\u5f0f\u4e0b\u9ed8\u8ba4\u4e0d\u53d1\u9001 reasoning \u53c2\u6570\u3002";
    private ChatSessionListItemViewModel? _selectedChatSession;
    private ChatPromptPresetViewModel? _selectedChatPreset;
    private bool _isBatchRankingApplyToastVisible;
    private string _batchRankingApplyToastMessage = string.Empty;
    private CancellationTokenSource? _batchRankingApplyToastCancellationSource;
    private string _lastRunAt = "从未运行";
    private string _networkSummary = "运行后显示公网 IP、网卡和 Ping。";
    private string _adapterSummary = "暂无网卡快照。";
    private string _pingSummary = "暂无 Ping 结果。";
    private string _webApiSummary = "运行后显示地区和 Trace 结果。";
    private string _webApiRawTrace = string.Empty;
    private string _selectedStunTransportKey = "udp";
    private string _stunServer = DefaultStunServerHost;
    private string _stunSummary = "运行后显示映射地址和 NAT 判断。";
    private string _stunAttributeSummary = string.Empty;
    private string _proxyBaseUrl = string.Empty;
    private string _proxyApiKey = string.Empty;
    private string _proxyModel = string.Empty;
    private string _applicationCenterBaseUrl = string.Empty;
    private string _applicationCenterApiKey = string.Empty;
    private string _applicationCenterModel = string.Empty;
    private string _proxyTimeoutSecondsText = "20";
    private bool _proxyIgnoreTlsErrors;
    private string _proxySeriesRoundsText = "5";
    private string _proxySeriesDelayMsText = "1200";
    private bool _isProxyBatchEditorOpen;
    private bool _isProxyModelPickerOpen;
    private bool _isProxyMultiModelPickerOpen;
    private bool _isProxyTrendChartOpen;
    private bool _isProxyTrendChartAutoOpenSuppressed;
    private bool _isProxyChartImageOnlyMode;
    private ProxyChartRetryMode _proxyChartRetryMode;
    private CancellationTokenSource? _currentProxyOperationCancellationSource;
    private bool _proxyCancellationRequestedByUser;
    private ProxySingleExecutionMode _lastProxySingleExecutionMode = ProxySingleExecutionMode.Basic;
    private ProxySingleExecutionPlan? _currentProxySingleExecutionPlan;
    private string _proxyChartRetryButtonText = "重试基础诊断";
    private readonly List<ProxyDiagnosticsResult> _proxySingleChartRuns = [];
    private readonly List<ProxyDiagnosticsResult> _proxyStabilityChartRounds = [];
    private int _proxyChartRequestedRounds;
    private int _proxyChartDelayMilliseconds;
    private bool _suppressProxyCatalogSelectionApply;
    private ProxyModelPickerTarget _proxyModelPickerTarget = ProxyModelPickerTarget.DefaultModel;
    private ProxyBatchEditorItemViewModel? _proxyBatchTemplateModelTargetRow;
    private string _proxyModelCatalogFilterText = string.Empty;
    private string _proxyMultiModelCatalogFilterText = string.Empty;
    private string? _selectedProxyCatalogModel;
    private bool _isChatCandidateModelDropDownOpen;
    private readonly Dictionary<string, int> _proxyModelContextWindows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _codexWireApiCompatibilityByEndpointModel = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _proxySelectedMultiModelNames = [];
    private string _proxyModelCatalogSummary = "拉取后显示模型数量和推荐项。";
    private string _proxyModelCatalogDetail = "尚未拉取模型列表。";
    private string _proxySummary = "填好 Base URL、API Key、模型后即可开始测试。";
    private string _proxyDetail = "\u57FA\u7840\u5355\u6B21\u8BCA\u65AD\u4F1A\u6D4B\u8BD5 /models\u3001\u4E00\u6B21\u975E\u6D41\u5F0F\u8BF7\u6C42\u3001\u4E00\u6B21\u6D41\u5F0F\u8BF7\u6C42\u3001Responses \u548C\u7ED3\u6784\u5316\u8F93\u51FA\uFF1B\u6DF1\u5EA6\u5355\u6B21\u8BCA\u65AD\u4F1A\u5728\u6B64\u57FA\u7840\u4E0A\u8FFD\u52A0\u590D\u6742\u63A2\u9488\u3002";
    private string _proxyStabilitySummary = "运行后显示稳定性摘要。";
    private string _proxyStabilityDetail = "暂无稳定性序列结果。";
    private string _historySummary = "暂无历史记录。";

    private string _transparentProxyPortText = "17880";
    private string _transparentProxyRoutesText = string.Empty;
    private TransparentProxyRouteEditorItemViewModel? _selectedTransparentProxyRouteEditorItem;
    private string _transparentProxyRateLimitPerMinuteText = "60";
    private string _transparentProxyMaxConcurrencyText = "8";
    private bool _transparentProxyEnableFallback = true;
    private bool _transparentProxyEnableCache = true;
    private string _transparentProxyCacheTtlSecondsText = "60";
    private bool _transparentProxyRewriteModel = true;
    private bool _isTransparentProxyRunning;
    private string _transparentProxyStatusSummary = "本地透明代理未启动。";
    private string _transparentProxyMetricsSummary = "等待请求进入本地入口。";
    private string _transparentProxyRoutingSummary = "路由表为空时可从当前接口和批量候选生成。";
    private string _transparentProxyHealthSummary = "健康检查：未监听。";
    private string _transparentProxyProtocolSummary = "协议探测：尚未运行。";
    private string _transparentProxyTotalRequestsText = "0";
    private string _transparentProxySuccessRateText = "-";
    private string _transparentProxyActiveRequestsText = "0";
    private string _transparentProxyFallbackRequestsText = "0";
    private string _transparentProxyCacheHitsText = "0";
    private string _transparentProxyP95LatencyText = "-";
    private string _transparentProxyTotalTokensText = "0 tokens";
    private string _transparentProxyTokensPerSecondText = "0 tok/s";
    private string _transparentProxyTokenMeterPrimaryText = "0 tokens";
    private string _transparentProxyTokenMeterSecondaryText = "等待请求";
    private string _transparentProxyTokenMeterAccentBrush = "#64748B";
    private long _transparentProxyLatestTotalOutputTokens;
    private double _transparentProxyLatestTokensPerSecond;
    private DateTimeOffset? _transparentProxyLatestTokenActivityAt;
    private bool _transparentProxyLatestIsRunning;
    private CancellationTokenSource? _transparentProxyAutoDiscoveryCancellationSource;
    private bool _isRefreshingTransparentProxyRouteEditor;
    private bool _isUpdatingTransparentProxyRoutesTextFromEditor;
    private readonly Dictionary<string, TransparentProxyProtocolSnapshot> _transparentProxyProtocolSnapshots = new(StringComparer.OrdinalIgnoreCase);

    private ProxySingleExecutionMode GetEffectiveSingleExecutionMode()
        => _currentProxySingleExecutionPlan?.Mode ?? _lastProxySingleExecutionMode;

    private string GetSingleProxyExecutionDisplayName()
        => GetSingleProxyExecutionDisplayName(GetEffectiveSingleExecutionMode());

    private static string GetSingleProxyExecutionDisplayName(ProxySingleExecutionMode mode)
        => mode == ProxySingleExecutionMode.Deep ? "深度单次诊断" : "基础单次诊断";

    private string GetSingleProxyOutputTitle()
        => GetEffectiveSingleExecutionMode() == ProxySingleExecutionMode.Deep
            ? "\u63A5\u53E3\u6DF1\u5EA6\u5355\u6B21\u8FD4\u56DE"
            : "\u63A5\u53E3\u57FA\u7840\u5355\u6B21\u8FD4\u56DE";

    private static string NormalizeStunServerHost(string? serverHost)
    {
        var normalized = serverHost?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return DefaultStunServerHost;
        }

        return normalized;
    }
}
