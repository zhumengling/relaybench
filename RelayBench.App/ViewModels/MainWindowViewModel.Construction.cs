using System.Collections.ObjectModel;
using RelayBench.App.Infrastructure;
using RelayBench.Core.Models;

namespace RelayBench.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    public MainWindowViewModel()
    {
        SpeedTestProfiles = new ObservableCollection<SpeedTestProfile>(_cloudflareSpeedTestService.GetProfiles());
        PortScanProfiles = new ObservableCollection<PortScanProfile>(_portScanDiagnosticsService.GetProfiles());

        DashboardCards =
        [
            new DashboardCardViewModel
            {
                Title = "网络",
                Status = "待运行",
                Detail = "公网 IP、网卡清单与基础 Ping"
            },
            new DashboardCardViewModel
            {
                Title = "网页 API",
                Status = "待运行",
                Detail = "读取 chatgpt.com/cdn-cgi/trace 并判断网页 API 区域可用性"
            },
            new DashboardCardViewModel
            {
                Title = "NAT / STUN",
                Status = "待运行",
                Detail = "映射地址、响应来源与 OTHER-ADDRESS 观察"
            },
            new DashboardCardViewModel
            {
                Title = "\u63A5\u53E3",
                Status = "未配置",
                Detail = "\u517C\u5BB9 OpenAI \u534F\u8BAE\u7684\u6A21\u578B\u3001\u804A\u5929\u3001\u6D41\u5F0F\u3001\u7A33\u5B9A\u6027\u4E0E\u6279\u91CF\u7AD9\u70B9\u5BF9\u6BD4"
            },
            new DashboardCardViewModel
            {
                Title = "测速",
                Status = "待运行",
                Detail = "Cloudflare 风格的延迟、抖动、下载、上传与丢包"
            },
            new DashboardCardViewModel
            {
                Title = "路由 / MTR",
                Status = "待运行",
                Detail = "tracert、逐跳丢包采样与真实地图渲染"
            },
            new DashboardCardViewModel
            {
                Title = "端口扫描",
                Status = "未知",
                Detail = "内置异步 TCP / UDP 轻量扫描、服务识别、批量任务与结果导出"
            },
            new DashboardCardViewModel
            {
                Title = "IP / 分流",
                Status = "待运行",
                Detail = "本地网卡、多出口对比、DNS 分析与 HTTPS 可达性"
            }
        ];

        RunQuickSuiteCommand = new AsyncRelayCommand(RunQuickSuiteAsync, CanRun);
        ExportCurrentReportCommand = new AsyncRelayCommand(ExportCurrentReportAsync, CanRun);
        RunNetworkCommand = new AsyncRelayCommand(RunNetworkAsync, CanRun);
        RunWebApiTraceCommand = new AsyncRelayCommand(RunWebApiTraceAsync, CanRun);
        RunClientApiDiagnosticsCommand = new AsyncRelayCommand(RunClientApiDiagnosticsAsync, CanRun);
        ApplyCurrentInterfaceToCodexAppsCommand = new AsyncRelayCommand(ApplyCurrentInterfaceToCodexAppsAsync, CanApplyCurrentInterfaceToCodexApps, onError: HandleNonFatalCommandException);
        RunStunCommand = new AsyncRelayCommand(RunStunAsync, CanRun);
        FetchProxyModelsCommand = new AsyncRelayCommand(FetchDefaultProxyModelsWithGlobalProgressAsync, CanRun);
        FetchProxyBatchSharedModelsCommand = new AsyncRelayCommand(FetchProxyBatchSharedModelsWithGlobalProgressAsync, CanRun);
        FetchProxyBatchEntryModelsCommand = new AsyncRelayCommand(FetchProxyBatchEntryModelsWithGlobalProgressAsync, CanRun);
        FetchProxyCapabilityModelsCommand = new AsyncRelayCommand<string?>(FetchProxyCapabilityModelsWithGlobalProgressAsync, _ => CanRun(), onError: HandleNonFatalCommandException);
        OpenProxyMultiModelPickerCommand = new AsyncRelayCommand(OpenProxyMultiModelPickerAsync, CanRun);
        ToggleProxyCapabilityConfigCommand = new AsyncRelayCommand(ToggleProxyCapabilityConfigAsync, CanRun);
        CloseProxyMultiModelPickerCommand = new AsyncRelayCommand(CloseProxyMultiModelPickerAsync);
        ConfirmProxyMultiModelPickerCommand = new AsyncRelayCommand(ConfirmProxyMultiModelPickerAsync);
        ClearProxyMultiModelSelectionCommand = new AsyncRelayCommand(ClearProxyMultiModelSelectionAsync);
        OpenProxyBatchEditorCommand = new AsyncRelayCommand(OpenProxyBatchEditorAsync, CanRun);
        CloseProxyBatchEditorCommand = new AsyncRelayCommand(CloseProxyBatchEditorAsync);
        AddCurrentProxyBaseUrlToBatchCommand = new AsyncRelayCommand(AddCurrentProxyBaseUrlToBatchAsync, onError: HandleNonFatalCommandException);
        CommitProxyBatchEditorItemCommand = new AsyncRelayCommand(CommitProxyBatchEditorItemAsync, onError: HandleNonFatalCommandException);
        AddProxyBatchEditorItemCommand = new AsyncRelayCommand(AddProxyBatchEditorItemAsync, onError: HandleNonFatalCommandException);
        UpdateProxyBatchEditorItemCommand = new AsyncRelayCommand(UpdateProxyBatchEditorItemAsync, onError: HandleNonFatalCommandException);
        DeleteProxyBatchEditorItemCommand = new AsyncRelayCommand(DeleteProxyBatchEditorItemAsync, onError: HandleNonFatalCommandException);
        ResetProxyBatchEditorFormCommand = new AsyncRelayCommand(ResetProxyBatchEditorFormAsync, onError: HandleNonFatalCommandException);
        PreviewProxyBatchSharedImportCommand = new AsyncRelayCommand(PreviewProxyBatchSharedImportAsync, onError: HandleNonFatalCommandException);
        ImportProxyBatchSharedEntriesCommand = new AsyncRelayCommand(ImportProxyBatchSharedEntriesAsync, onError: HandleNonFatalCommandException);
        PreviewProxyBatchIndependentImportCommand = new AsyncRelayCommand(PreviewProxyBatchIndependentImportAsync, onError: HandleNonFatalCommandException);
        ImportProxyBatchIndependentEntriesCommand = new AsyncRelayCommand(ImportProxyBatchIndependentEntriesAsync, onError: HandleNonFatalCommandException);
        AddProxyBatchTemplateRowCommand = new AsyncRelayCommand(AddProxyBatchTemplateRowAsync, CanRun, onError: HandleNonFatalCommandException);
        DeleteProxyBatchTemplateRowCommand = new AsyncRelayCommand<ProxyBatchEditorItemViewModel?>(DeleteProxyBatchTemplateRowAsync, item => CanRun() && item is not null, onError: HandleNonFatalCommandException);
        PasteProxyBatchTemplateRowsCommand = new AsyncRelayCommand(PasteProxyBatchTemplateRowsAsync, CanRun, onError: HandleNonFatalCommandException);
        ApplyProxyBatchTemplateDefaultsCommand = new AsyncRelayCommand(ApplyProxyBatchTemplateDefaultsAsync, CanRun, onError: HandleNonFatalCommandException);
        ClearProxyBatchTemplateEmptyRowsCommand = new AsyncRelayCommand(ClearProxyBatchTemplateEmptyRowsAsync, CanRun, onError: HandleNonFatalCommandException);
        FetchProxyBatchTemplateRowModelsCommand = new AsyncRelayCommand<ProxyBatchEditorItemViewModel?>(FetchProxyBatchTemplateRowModelsAsync, item => CanRun() && item is not null, onError: HandleNonFatalCommandException);
        CloseProxyModelPickerCommand = new AsyncRelayCommand(CloseProxyModelPickerAsync);
        OpenProxyTrendChartCommand = new AsyncRelayCommand(OpenProxyTrendChartAsync);
        OpenProxySingleChartCommand = new AsyncRelayCommand(OpenCurrentSingleStationChartAsync);
        OpenProxyConcurrencyChartCommand = new AsyncRelayCommand(OpenProxyConcurrencyChartAsync);
        OpenBatchComparisonChartCommand = new AsyncRelayCommand(OpenBatchComparisonChartAsync);
        OpenBatchDeepComparisonChartCommand = new AsyncRelayCommand(OpenBatchDeepComparisonChartAsync);
        OpenProxyEndpointHistoryCommand = new AsyncRelayCommand(OpenProxyEndpointHistoryAsync);
        CloseProxyEndpointHistoryCommand = new AsyncRelayCommand(CloseProxyEndpointHistoryAsync);
        ApplyProxyEndpointHistoryItemCommand = new AsyncRelayCommand<ProxyEndpointHistoryItemViewModel?>(ApplyProxyEndpointHistoryItemAsync);
        ClearProxyEndpointHistoryCommand = new AsyncRelayCommand(ClearProxyEndpointHistoryAsync);
        CloseProxyTrendChartCommand = new AsyncRelayCommand(CloseProxyTrendChartAsync);
        StopCurrentProxyTestCommand = new AsyncRelayCommand(StopCurrentProxyTestAsync, CanStopCurrentProxyTestAction);
        CloseOfficialApiTraceDialogCommand = new AsyncRelayCommand(CloseOfficialApiTraceDialogAsync);
        RetryProxyChartCommand = new AsyncRelayCommand(RetryProxyChartAsync, CanRetryProxyChart, onError: HandleNonFatalCommandException);
        ToggleProxyChartViewCommand = new AsyncRelayCommand(ToggleProxyChartViewAsync, CanToggleProxyChartViewAction);
        ToggleProxyChartImageOnlyModeCommand = new AsyncRelayCommand(ToggleProxyChartImageOnlyModeAsync, CanToggleProxyChartImageOnlyModeAction);
        RunProxyCommand = new AsyncRelayCommand(RunProxyWithValidationAsync, CanRun);
        RunProxyDeepCommand = new AsyncRelayCommand(RunProxyDeepWithValidationAsync, CanRun);
        RunProxySeriesCommand = new AsyncRelayCommand(RunProxySeriesWithValidationAsync, CanRun);
        RunSelectedSingleStationModeCommand = new AsyncRelayCommand(RunSelectedSingleStationModeAsync, CanRun);
        RunProxyBatchCommand = new AsyncRelayCommand(RunProxyBatchWithValidationAsync, CanRun);
        RunSelectedBatchDeepTestsCommand = new AsyncRelayCommand(RunSelectedBatchDeepTestsAsync, CanRunSelectedBatchDeepTestsAction);
        ApplyRankingRowToCodexAppsCommand = new AsyncRelayCommand<ProxyBatchRankingRowViewModel?>(ApplyRankingRowToCodexAppsAsync, CanApplyRankingRowToCodexApps, onError: HandleNonFatalCommandException);
        RunSpeedTestCommand = new AsyncRelayCommand(RunSpeedTestWithGlobalProgressAsync, CanRun);
        RunRouteCommand = new AsyncRelayCommand(RunRouteWithGlobalProgressAsync, CanRun);
        RunRouteContinuousCommand = new AsyncRelayCommand(RunRouteContinuousWithGlobalProgressAsync, CanRun);
        StopRouteContinuousCommand = new AsyncRelayCommand(StopRouteContinuousAsync, CanStopRouteContinuous);
        DetectPortScanEngineCommand = new AsyncRelayCommand(DetectPortScanEngineWithGlobalProgressAsync, CanRun);
        RunPortScanCommand = new AsyncRelayCommand(RunPortScanWithGlobalProgressAsync, CanRun);
        RunPortScanBatchCommand = new AsyncRelayCommand(RunPortScanBatchWithGlobalProgressAsync, CanRun);
        ExportPortScanCsvCommand = new AsyncRelayCommand(ExportPortScanCsvAsync, CanExportPortScanResults);
        ExportPortScanExcelCommand = new AsyncRelayCommand(ExportPortScanExcelAsync, CanExportPortScanResults);
        RunSplitRoutingCommand = new AsyncRelayCommand(RunSplitRoutingWithGlobalProgressAsync, CanRun);
        RunIpRiskReviewCommand = new AsyncRelayCommand(RunIpRiskReviewWithGlobalProgressAsync, CanRun);
        ConfirmConfirmationDialogCommand = new AsyncRelayCommand(ConfirmConfirmationDialogAsync);
        CancelConfirmationDialogCommand = new AsyncRelayCommand(CancelConfirmationDialogAsync);
        OpenAboutDialogCommand = new AsyncRelayCommand(OpenAboutDialogAsync);
        CloseAboutDialogCommand = new AsyncRelayCommand(CloseAboutDialogAsync);
        OpenProjectHomepageCommand = new AsyncRelayCommand(OpenProjectHomepageAsync);

        ResetIpRiskPresentation();
        LoadState();
        RefreshFilteredPortScanFindings();
        RefreshPortScanBatchSummary();
        RefreshPortScanExportSummary();
        RefreshPortScanExportCommands();
        RefreshStunServerOptions(syncCurrentHost: true);
        SaveState();
        RefreshProxyTrendView();
        RefreshReportArchiveView();
    }
}
