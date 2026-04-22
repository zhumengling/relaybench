using System.Collections.ObjectModel;
using NetTest.App.Infrastructure;
using NetTest.App.Services;
using NetTest.Core.Models;
using NetTest.Core.Services;

namespace NetTest.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private const string DefaultStunServerHost = StunServerPresetCatalog.RecommendedUdpNatReviewHost;

    private readonly BasicNetworkDiagnosticsService _networkDiagnosticsService = new();
    private readonly ChatGptTraceService _chatGptTraceService = new();
    private readonly StunProbeService _stunProbeService = new();
    private readonly ProxyDiagnosticsService _proxyDiagnosticsService = new();
    private readonly CloudflareSpeedTestService _cloudflareSpeedTestService = new();
    private readonly RouteDiagnosticsService _routeDiagnosticsService = new();
    private readonly PortScanDiagnosticsService _portScanDiagnosticsService = new();
    private readonly SplitRoutingDiagnosticsService _splitRoutingDiagnosticsService = new();
    private readonly DiagnosticReportService _diagnosticReportService = new();
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

    private bool _isBusy;
    private string _statusMessage = "准备就绪，随时可以开始诊断。";
    private bool _isBatchRankingApplyToastVisible;
    private string _batchRankingApplyToastMessage = string.Empty;
    private CancellationTokenSource? _batchRankingApplyToastCancellationSource;
    private string _lastRunAt = "从未运行";
    private string _networkSummary = "运行网络检测后，这里会显示公网 IP、活动网卡和 Ping 摘要。";
    private string _adapterSummary = "尚未采集网卡快照。";
    private string _pingSummary = "暂无 Ping 结果。";
    private string _chatGptSummary = "运行官方 API 可用性检测后，这里会显示 loc、colo 和支持地区判断。";
    private string _chatGptRawTrace = string.Empty;
    private string _selectedStunTransportKey = "udp";
    private string _stunServer = DefaultStunServerHost;
    private string _stunSummary = "运行 STUN 检测后，这里会显示映射地址与 OTHER-ADDRESS 行为。";
    private string _stunAttributeSummary = string.Empty;
    private string _proxyBaseUrl = string.Empty;
    private string _proxyApiKey = string.Empty;
    private string _proxyModel = string.Empty;
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
    private readonly List<string> _proxySelectedMultiModelNames = [];
    private string _proxyModelCatalogSummary = "点击拉取模型列表后，这里会显示可用模型数量、推荐模型和边缘观察。";
    private string _proxyModelCatalogDetail = "尚未拉取模型列表。";
    private string _proxySummary = "\u586B\u5199 Base URL\u3001API Key \u548C\u6A21\u578B\u540E\uFF0C\u5373\u53EF\u8FD0\u884C\u57FA\u7840\u6216\u6DF1\u5EA6\u63A5\u53E3\u68C0\u6D4B\u3002";
    private string _proxyDetail = "\u57FA\u7840\u5355\u6B21\u8BCA\u65AD\u4F1A\u6D4B\u8BD5 /models\u3001\u4E00\u6B21\u975E\u6D41\u5F0F\u8BF7\u6C42\u3001\u4E00\u6B21\u6D41\u5F0F\u8BF7\u6C42\u3001Responses \u548C\u7ED3\u6784\u5316\u8F93\u51FA\uFF1B\u6DF1\u5EA6\u5355\u6B21\u8BCA\u65AD\u4F1A\u5728\u6B64\u57FA\u7840\u4E0A\u8FFD\u52A0\u590D\u6742\u63A2\u9488\u3002";
    private string _proxyStabilitySummary = "运行稳定性序列后，这里会显示健康分与稳定性摘要。";
    private string _proxyStabilityDetail = "暂无稳定性序列结果。";
    private string _historySummary = "还没有保存的诊断历史。";

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
