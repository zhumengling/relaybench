using System.Collections.ObjectModel;
using NetTest.App.Infrastructure;
using NetTest.Core.Models;

namespace NetTest.App.ViewModels;

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
                Title = "官方 API",
                Status = "待运行",
                Detail = "读取 chatgpt.com/cdn-cgi/trace 并判断官方 API 区域可用性"
            },
            new DashboardCardViewModel
            {
                Title = "NAT / STUN",
                Status = "待运行",
                Detail = "映射地址、响应来源与 OTHER-ADDRESS 观察"
            },
            new DashboardCardViewModel
            {
                Title = "中转站",
                Status = "未配置",
                Detail = "兼容 OpenAI 的模型、聊天、流式、稳定性与入口组对比"
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
        RunChatGptTraceCommand = new AsyncRelayCommand(RunChatGptTraceAsync, CanRun);
        RunClientApiDiagnosticsCommand = new AsyncRelayCommand(RunClientApiDiagnosticsAsync, CanRun);
        RunStunCommand = new AsyncRelayCommand(RunStunAsync, CanRun);
        FetchProxyModelsCommand = new AsyncRelayCommand(FetchDefaultProxyModelsAsync, CanRun);
        FetchProxyBatchSharedModelsCommand = new AsyncRelayCommand(FetchProxyBatchSharedModelsAsync, CanRun);
        FetchProxyBatchEntryModelsCommand = new AsyncRelayCommand(FetchProxyBatchEntryModelsAsync, CanRun);
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
        OpenBatchComparisonChartCommand = new AsyncRelayCommand(OpenBatchComparisonChartAsync);
        OpenBatchDeepComparisonChartCommand = new AsyncRelayCommand(OpenBatchDeepComparisonChartAsync);
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
        RunSpeedTestCommand = new AsyncRelayCommand(RunSpeedTestAsync, CanRun);
        RunRouteCommand = new AsyncRelayCommand(RunRouteAsync, CanRun);
        RunRouteContinuousCommand = new AsyncRelayCommand(RunRouteContinuousAsync, CanRun);
        StopRouteContinuousCommand = new AsyncRelayCommand(StopRouteContinuousAsync, CanStopRouteContinuous);
        DetectPortScanEngineCommand = new AsyncRelayCommand(DetectPortScanEngineAsync, CanRun);
        RunPortScanCommand = new AsyncRelayCommand(RunPortScanAsync, CanRun);
        RunPortScanBatchCommand = new AsyncRelayCommand(RunPortScanBatchAsync, CanRun);
        ExportPortScanCsvCommand = new AsyncRelayCommand(ExportPortScanCsvAsync, CanExportPortScanResults);
        ExportPortScanExcelCommand = new AsyncRelayCommand(ExportPortScanExcelAsync, CanExportPortScanResults);
        RunSplitRoutingCommand = new AsyncRelayCommand(RunSplitRoutingAsync, CanRun);

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
