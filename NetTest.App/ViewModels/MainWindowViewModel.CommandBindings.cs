using System.Collections.ObjectModel;
using NetTest.App.Infrastructure;

namespace NetTest.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    public void PersistState()
        => SaveState();

    public ObservableCollection<DashboardCardViewModel> DashboardCards { get; }

    public ObservableCollection<string> ProxyCatalogModels { get; } = [];

    public ObservableCollection<string> VisibleProxyCatalogModels { get; } = [];

    public ObservableCollection<ProxyBatchEditorItemViewModel> ProxyBatchEditorItems { get; } = [];

    public ObservableCollection<ProxyBatchRankingRowViewModel> ProxyBatchRankingRows { get; } = [];

    public ObservableCollection<SelectionOption> WorkbenchPageOptions { get; } =
    [
        new("single-station", "单站测试"),
        new("batch-comparison", "批量对比"),
        new("network-review", "网络复核"),
        new("history-reports", "历史报告")
    ];

    public ObservableCollection<SelectionOption> SingleStationModeOptions { get; } =
    [
        new("quick", "快速测试"),
        new("stability", "稳定性测试"),
        new("deep", "深度测试")
    ];

    public ObservableCollection<SelectionOption> NetworkReviewIssueOptions { get; } =
    [
        new("relay-unavailable", "中转站不可用"),
        new("high-ttft", "TTFT 很高"),
        new("high-jitter", "波动很大"),
        new("geo-unlock", "地区 / 解锁异常"),
        new("dns-routing", "DNS / 分流怀疑")
    ];

    public ObservableCollection<SelectionOption> NetworkReviewToolOptions { get; } =
    [
        new(NetworkReviewToolNetwork, "基础网络"),
        new(NetworkReviewToolOfficialApi, "官方 API"),
        new(NetworkReviewToolSpeed, "测速"),
        new(NetworkReviewToolRoute, "路由 / MTR"),
        new(NetworkReviewToolSplitRouting, "IP 与分流"),
        new(NetworkReviewToolStun, "NAT / STUN"),
        new(NetworkReviewToolPortScan, "端口扫描")
    ];

    public AsyncRelayCommand RunQuickSuiteCommand { get; }

    public AsyncRelayCommand ExportCurrentReportCommand { get; }

    public AsyncRelayCommand RunNetworkCommand { get; }

    public AsyncRelayCommand RunChatGptTraceCommand { get; }

    public AsyncRelayCommand RunStunCommand { get; }

    public AsyncRelayCommand FetchProxyModelsCommand { get; }

    public AsyncRelayCommand FetchProxyBatchSharedModelsCommand { get; }

    public AsyncRelayCommand FetchProxyBatchEntryModelsCommand { get; }

    public AsyncRelayCommand OpenProxyBatchEditorCommand { get; }

    public AsyncRelayCommand CloseProxyBatchEditorCommand { get; }

    public AsyncRelayCommand AddCurrentProxyBaseUrlToBatchCommand { get; }

    public AsyncRelayCommand CommitProxyBatchEditorItemCommand { get; }

    public AsyncRelayCommand AddProxyBatchEditorItemCommand { get; }

    public AsyncRelayCommand UpdateProxyBatchEditorItemCommand { get; }

    public AsyncRelayCommand DeleteProxyBatchEditorItemCommand { get; }

    public AsyncRelayCommand ResetProxyBatchEditorFormCommand { get; }

    public AsyncRelayCommand CloseProxyModelPickerCommand { get; }

    public AsyncRelayCommand OpenProxyTrendChartCommand { get; }

    public AsyncRelayCommand OpenBatchComparisonChartCommand { get; }

    public AsyncRelayCommand OpenBatchDeepComparisonChartCommand { get; }

    public AsyncRelayCommand CloseProxyTrendChartCommand { get; }

    public AsyncRelayCommand RetryProxyChartCommand { get; }

    public AsyncRelayCommand ToggleProxyChartViewCommand { get; }

    public AsyncRelayCommand ToggleProxyChartImageOnlyModeCommand { get; }

    public AsyncRelayCommand RunProxyCommand { get; }

    public AsyncRelayCommand RunProxyDeepCommand { get; }

    public AsyncRelayCommand RunProxySeriesCommand { get; }

    public AsyncRelayCommand RunSelectedSingleStationModeCommand { get; }

    public AsyncRelayCommand RunProxyBatchCommand { get; }

    public AsyncRelayCommand RunSelectedBatchDeepTestsCommand { get; }

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
}
