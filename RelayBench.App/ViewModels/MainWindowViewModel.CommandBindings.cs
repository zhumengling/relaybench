using System.Collections.ObjectModel;
using RelayBench.App.Infrastructure;
using RelayBench.App.Services;
using RelayBench.Core.Models;

namespace RelayBench.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    public void PersistState()
        => SaveState();

    public ObservableCollection<DashboardCardViewModel> DashboardCards { get; }

    public ObservableCollection<string> ProxyCatalogModels { get; } = [];

    public ObservableCollection<string> VisibleProxyCatalogModels { get; } = [];

    public ObservableCollection<ProxySelectableModelItemViewModel> VisibleProxyMultiModelCatalogItems { get; } = [];

    public ObservableCollection<ProxyBatchEditorItemViewModel> ProxyBatchEditorItems { get; } = [];

    public ObservableCollection<ProxyBatchEditorItemViewModel> ProxyBatchTemplateDraftItems { get; } = [];

    public ObservableCollection<ProxyBatchSiteGroupViewModel> ProxyBatchSiteGroups { get; } = [];

    public ObservableCollection<ProxyBatchRankingRowViewModel> ProxyBatchRankingRows { get; } = [];

    public ObservableCollection<ExitIpRiskSourceResult> IpRiskSourceResults { get; } = [];

    public ObservableCollection<IpRiskSummaryBadgeViewModel> IpRiskSummaryBadges { get; } = [];

    public ObservableCollection<IpRiskIndicatorCardViewModel> IpRiskIndicatorCards { get; } = [];

    public ObservableCollection<IpRiskSourceRowViewModel> IpRiskSourceRows { get; } = [];

    public ObservableCollection<SelectionOption> StunTransportOptions { get; } =
        new(StunServerPresetCatalog.BuildTransportOptions());

    public ObservableCollection<SelectionOption> VisibleStunServerOptions { get; } = [];

    public ObservableCollection<SelectionOption> WorkbenchPageOptions { get; } =
    [
        new("interface-diagnostics", "接口诊断"),
        new("batch-evaluation", "批量评测"),
        new("application-center", "应用接入"),
        new("network-review", "网络复核"),
        new("history-reports", "历史报告")
    ];

    public ObservableCollection<SelectionOption> SingleStationModeOptions { get; } =
    [
        new("quick", "快速测试"),
        new("stability", "稳定性测试"),
        new("deep", "深度测试"),
        new("concurrency", "\u5E76\u53D1\u538B\u6D4B")
    ];

    public ObservableCollection<SelectionOption> NetworkReviewIssueOptions { get; } =
    [
        new("interface-unavailable", "接口不可用"),
        new("high-ttft", "TTFT 很高"),
        new("high-jitter", "波动很大"),
        new("geo-unlock", "地区 / 解锁异常"),
        new("dns-routing", "DNS / 分流怀疑")
    ];

    public ObservableCollection<SelectionOption> NetworkReviewToolOptions { get; } =
    [
        new(NetworkReviewToolNetwork, "基础网络"),
        new(NetworkReviewToolOfficialApi, "网页 API"),
        new(NetworkReviewToolSpeed, "测速"),
        new(NetworkReviewToolRoute, "路由 / MTR"),
        new(NetworkReviewToolSplitRouting, "IP 与分流"),
        new(NetworkReviewToolIpRisk, "IP 风险"),
        new(NetworkReviewToolStun, "NAT / STUN"),
        new(NetworkReviewToolPortScan, "端口扫描")
    ];

    public AsyncRelayCommand RunQuickSuiteCommand { get; }

    public AsyncRelayCommand ExportCurrentReportCommand { get; }

    public AsyncRelayCommand RunNetworkCommand { get; }

    public AsyncRelayCommand RunWebApiTraceCommand { get; }

    public AsyncRelayCommand RunClientApiDiagnosticsCommand { get; }

    public AsyncRelayCommand ApplyCurrentInterfaceToCodexAppsCommand { get; }

    public AsyncRelayCommand RunStunCommand { get; }

    public AsyncRelayCommand FetchProxyModelsCommand { get; }

    public AsyncRelayCommand FetchProxyBatchSharedModelsCommand { get; }

    public AsyncRelayCommand FetchProxyBatchEntryModelsCommand { get; }

    public AsyncRelayCommand<string?> FetchProxyCapabilityModelsCommand { get; }

    public AsyncRelayCommand OpenProxyMultiModelPickerCommand { get; }

    public AsyncRelayCommand ToggleProxyCapabilityConfigCommand { get; }

    public AsyncRelayCommand CloseProxyMultiModelPickerCommand { get; }

    public AsyncRelayCommand ConfirmProxyMultiModelPickerCommand { get; }

    public AsyncRelayCommand ClearProxyMultiModelSelectionCommand { get; }

    public AsyncRelayCommand OpenProxyBatchEditorCommand { get; }

    public AsyncRelayCommand CloseProxyBatchEditorCommand { get; }

    public AsyncRelayCommand AddCurrentProxyBaseUrlToBatchCommand { get; }

    public AsyncRelayCommand CommitProxyBatchEditorItemCommand { get; }

    public AsyncRelayCommand AddProxyBatchEditorItemCommand { get; }

    public AsyncRelayCommand UpdateProxyBatchEditorItemCommand { get; }

    public AsyncRelayCommand DeleteProxyBatchEditorItemCommand { get; }

    public AsyncRelayCommand ResetProxyBatchEditorFormCommand { get; }

    public AsyncRelayCommand PreviewProxyBatchSharedImportCommand { get; }

    public AsyncRelayCommand ImportProxyBatchSharedEntriesCommand { get; }

    public AsyncRelayCommand PreviewProxyBatchIndependentImportCommand { get; }

    public AsyncRelayCommand ImportProxyBatchIndependentEntriesCommand { get; }

    public AsyncRelayCommand AddProxyBatchTemplateRowCommand { get; }

    public AsyncRelayCommand<ProxyBatchEditorItemViewModel?> DeleteProxyBatchTemplateRowCommand { get; }

    public AsyncRelayCommand PasteProxyBatchTemplateRowsCommand { get; }

    public AsyncRelayCommand ApplyProxyBatchTemplateDefaultsCommand { get; }

    public AsyncRelayCommand ClearProxyBatchTemplateEmptyRowsCommand { get; }

    public AsyncRelayCommand<ProxyBatchEditorItemViewModel?> FetchProxyBatchTemplateRowModelsCommand { get; }

    public AsyncRelayCommand CloseProxyModelPickerCommand { get; }

    public AsyncRelayCommand OpenProxyTrendChartCommand { get; }

    public AsyncRelayCommand OpenProxySingleChartCommand { get; }

    public AsyncRelayCommand OpenProxyConcurrencyChartCommand { get; }

    public AsyncRelayCommand OpenBatchComparisonChartCommand { get; }

    public AsyncRelayCommand OpenBatchDeepComparisonChartCommand { get; }

    public AsyncRelayCommand OpenProxyEndpointHistoryCommand { get; }

    public AsyncRelayCommand CloseProxyEndpointHistoryCommand { get; }

    public AsyncRelayCommand<ProxyEndpointHistoryItemViewModel?> ApplyProxyEndpointHistoryItemCommand { get; }

    public AsyncRelayCommand ClearProxyEndpointHistoryCommand { get; }

    public AsyncRelayCommand CloseProxyTrendChartCommand { get; }

    public AsyncRelayCommand StopCurrentProxyTestCommand { get; }

    public AsyncRelayCommand RetryProxyChartCommand { get; }

    public AsyncRelayCommand ToggleProxyChartViewCommand { get; }

    public AsyncRelayCommand ToggleProxyChartImageOnlyModeCommand { get; }

    public AsyncRelayCommand RunProxyCommand { get; }

    public AsyncRelayCommand RunProxyDeepCommand { get; }

    public AsyncRelayCommand RunProxySeriesCommand { get; }

    public AsyncRelayCommand RunSelectedSingleStationModeCommand { get; }

    public AsyncRelayCommand RunProxyBatchCommand { get; }

    public AsyncRelayCommand ToggleBatchDeepSelectionCommand { get; }

    public AsyncRelayCommand RunSelectedBatchDeepTestsCommand { get; }

    public AsyncRelayCommand<ProxyBatchRankingRowViewModel?> ApplyRankingRowToCodexAppsCommand { get; }

    public AsyncRelayCommand RunSpeedTestCommand { get; }

    public AsyncRelayCommand RunRouteCommand { get; }

    public AsyncRelayCommand RunRouteContinuousCommand { get; }

    public AsyncRelayCommand StopRouteContinuousCommand { get; }

    public AsyncRelayCommand DetectPortScanEngineCommand { get; }

    public AsyncRelayCommand RunPortScanCommand { get; }

    public AsyncRelayCommand RunPortScanBatchCommand { get; }

    public AsyncRelayCommand ExportPortScanCsvCommand { get; }

    public AsyncRelayCommand ExportPortScanExcelCommand { get; }

    public AsyncRelayCommand RunSplitRoutingCommand { get; }

    public AsyncRelayCommand RunIpRiskReviewCommand { get; }

    public AsyncRelayCommand ConfirmConfirmationDialogCommand { get; }

    public AsyncRelayCommand CancelConfirmationDialogCommand { get; }

    public AsyncRelayCommand OpenAboutDialogCommand { get; }

    public AsyncRelayCommand CloseAboutDialogCommand { get; }

    public AsyncRelayCommand OpenProjectHomepageCommand { get; }
}
