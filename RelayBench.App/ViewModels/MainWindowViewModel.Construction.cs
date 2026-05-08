using System.Collections.ObjectModel;
using RelayBench.App.Infrastructure;
using RelayBench.App.Services;
using RelayBench.App.ViewModels.AdvancedTesting;
using RelayBench.Core.AdvancedTesting.Runners;
using RelayBench.Core.Models;

namespace RelayBench.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    public MainWindowViewModel()
    {
        _transparentProxyService = new TransparentProxyService(null, _codexOAuthService);
        _proxyEndpointProtocolProbeService = new(
            _proxyDiagnosticsService,
            _proxyEndpointModelCacheService);
        _transparentProxyProtocolDiscoveryService = new(
            _proxyDiagnosticsService,
            _proxyEndpointModelCacheService,
            _proxyEndpointProtocolProbeService);
        _transparentProxyService.LogEmitted += OnTransparentProxyLogEmitted;
        _transparentProxyService.MetricsChanged += OnTransparentProxyMetricsChanged;
        _codexOAuthService.CredentialsChanged += OnCodexOAuthCredentialsChanged;

        SpeedTestProfiles = new ObservableCollection<SpeedTestProfile>(_cloudflareSpeedTestService.GetProfiles());
        PortScanProfiles = new ObservableCollection<PortScanProfile>(_portScanDiagnosticsService.GetProfiles());
        AdvancedTestLab = new AdvancedTestLabViewModel(
            () => new(
                ProxyBaseUrl,
                ProxyApiKey,
                ProxyModel,
                ProxyIgnoreTlsErrors,
                ParseBoundedInt(ProxyTimeoutSecondsText, fallback: 20, min: 5, max: 300)),
            new AdvancedTestRunner(_proxyEndpointProtocolProbeService));

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
        FetchApplicationCenterProxyModelsCommand = new AsyncRelayCommand(FetchApplicationCenterProxyModelsWithGlobalProgressAsync, CanRun);
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
        ToggleProxyBatchTemplateRowsTestInclusionCommand = new AsyncRelayCommand(ToggleProxyBatchTemplateRowsTestInclusionAsync, CanRun, onError: HandleNonFatalCommandException);
        FetchProxyBatchTemplateRowModelsCommand = new AsyncRelayCommand<ProxyBatchEditorItemViewModel?>(FetchProxyBatchTemplateRowModelsAsync, item => CanRun() && item is not null, onError: HandleNonFatalCommandException);
        CloseProxyModelPickerCommand = new AsyncRelayCommand(CloseProxyModelPickerAsync);
        OpenProxyTrendChartCommand = new AsyncRelayCommand(OpenProxyTrendChartAsync);
        OpenProxySingleChartCommand = new AsyncRelayCommand(OpenCurrentSingleStationChartAsync);
        OpenProxyConcurrencyChartCommand = new AsyncRelayCommand(OpenProxyConcurrencyChartAsync);
        OpenBatchComparisonChartCommand = new AsyncRelayCommand(OpenBatchComparisonChartAsync);
        OpenBatchDeepComparisonChartCommand = new AsyncRelayCommand(OpenBatchDeepComparisonChartAsync);
        OpenProxyEndpointHistoryCommand = new AsyncRelayCommand(OpenProxyEndpointHistoryAsync);
        OpenApplicationCenterProxyEndpointHistoryCommand = new AsyncRelayCommand(OpenApplicationCenterProxyEndpointHistoryAsync);
        OpenAdvancedTestLabProxyEndpointHistoryCommand = new AsyncRelayCommand(OpenAdvancedTestLabProxyEndpointHistoryAsync);
        CloseProxyEndpointHistoryCommand = new AsyncRelayCommand(CloseProxyEndpointHistoryAsync);
        ApplyProxyEndpointHistoryItemCommand = new AsyncRelayCommand<ProxyEndpointHistoryItemViewModel?>(ApplyProxyEndpointHistoryItemAsync);
        ClearProxyEndpointHistoryCommand = new AsyncRelayCommand(ClearProxyEndpointHistoryAsync);
        CloseProxyTrendChartCommand = new AsyncRelayCommand(CloseProxyTrendChartAsync);
        StopCurrentProxyTestCommand = new AsyncRelayCommand(StopCurrentProxyTestAsync, CanStopCurrentProxyTestAction);
        CloseOfficialApiTraceDialogCommand = new AsyncRelayCommand(CloseOfficialApiTraceDialogAsync);
        CopyOfficialApiTraceDialogContentCommand = new AsyncRelayCommand(CopyOfficialApiTraceDialogContentAsync);
        RetryProxyChartCommand = new AsyncRelayCommand(RetryProxyChartAsync, CanRetryProxyChart, onError: HandleNonFatalCommandException);
        ToggleProxyChartViewCommand = new AsyncRelayCommand(ToggleProxyChartViewAsync, CanToggleProxyChartViewAction);
        ToggleProxyChartImageOnlyModeCommand = new AsyncRelayCommand(ToggleProxyChartImageOnlyModeAsync, CanToggleProxyChartImageOnlyModeAction);
        OpenProbeTraceCommand = new AsyncRelayCommand<ProxySingleCapabilityChartRowViewModel?>(OpenProbeTraceAsync, row => row?.HasTrace == true, onError: HandleNonFatalCommandException);
        RunProxyCommand = new AsyncRelayCommand(RunProxyWithValidationAsync, CanRun);
        RunProxyDeepCommand = new AsyncRelayCommand(RunProxyDeepWithValidationAsync, CanRun);
        RunProxySeriesCommand = new AsyncRelayCommand(RunProxySeriesWithValidationAsync, CanRun);
        RunSelectedSingleStationModeCommand = new AsyncRelayCommand(RunSelectedSingleStationModeAsync, CanRun);
        RunProxyBatchCommand = new AsyncRelayCommand(RunProxyBatchWithValidationAsync, CanRun);
        ToggleBatchDeepSelectionCommand = new AsyncRelayCommand(ToggleBatchDeepSelectionAsync, CanToggleBatchDeepSelectionAction);
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
        StartTransparentProxyCommand = new AsyncRelayCommand(StartTransparentProxyAsync, CanStartTransparentProxy, onError: HandleNonFatalCommandException);
        StopTransparentProxyCommand = new AsyncRelayCommand(StopTransparentProxyAsync, () => IsTransparentProxyRunning, onError: HandleNonFatalCommandException);
        RefreshTransparentProxyRoutesCommand = new AsyncRelayCommand(RefreshTransparentProxyRoutesFromWorkspaceAsync, onError: HandleNonFatalCommandException);
        GenerateTransparentProxyCandidateRoutesCommand = new AsyncRelayCommand(GenerateTransparentProxyCandidateRoutesAsync, CanRun, onError: HandleNonFatalCommandException);
        ProbeTransparentProxyProtocolsCommand = new AsyncRelayCommand(ProbeTransparentProxyProtocolsAsync, CanRun, onError: HandleNonFatalCommandException);
        RunTransparentProxySelfTestCommand = new AsyncRelayCommand(RunTransparentProxySelfTestAsync, CanRun, onError: HandleNonFatalCommandException);
        ApplyTransparentProxyToAppsCommand = new AsyncRelayCommand(ApplyTransparentProxyToAppsAsync, CanApplyTransparentProxyToApps, onError: HandleNonFatalCommandException);
        ApplyTransparentProxyToAdvancedLabCommand = new AsyncRelayCommand(ApplyTransparentProxyToAdvancedLabAsync, CanApplyTransparentProxyToAdvancedLab, onError: HandleNonFatalCommandException);
        ImportProxyBatchToTransparentProxyCommand = new AsyncRelayCommand(ImportProxyBatchToTransparentProxyAsync, onError: HandleNonFatalCommandException);
        ExportTransparentProxyRoutesToBatchCommand = new AsyncRelayCommand(ExportTransparentProxyRoutesToBatchAsync, onError: HandleNonFatalCommandException);
        AddTransparentProxyRouteEditorItemCommand = new AsyncRelayCommand(AddTransparentProxyRouteEditorItemAsync, onError: HandleNonFatalCommandException);
        ToggleTransparentProxySettingsDrawerCommand = new AsyncRelayCommand(ToggleTransparentProxySettingsDrawerAsync, onError: HandleNonFatalCommandException);
        ToggleTransparentProxyListenSettingsCommand = new AsyncRelayCommand(ToggleTransparentProxyListenSettingsAsync, onError: HandleNonFatalCommandException);
        ToggleTransparentProxyAppCaptureSettingsCommand = new AsyncRelayCommand(ToggleTransparentProxyAppCaptureSettingsAsync, onError: HandleNonFatalCommandException);
        RefreshTransparentProxyCaptureTargetsCommand = new AsyncRelayCommand(RefreshTransparentProxyCaptureTargetsAsync, onError: HandleNonFatalCommandException);
        RefreshTransparentProxyCaptureDiagnosticsCommand = new AsyncRelayCommand(RefreshTransparentProxyCaptureDiagnosticsAsync, onError: HandleNonFatalCommandException);
        CopyTransparentProxyPowerShellEnvCommand = new AsyncRelayCommand(CopyTransparentProxyPowerShellEnvAsync, onError: HandleNonFatalCommandException);
        CopyTransparentProxyCmdEnvCommand = new AsyncRelayCommand(CopyTransparentProxyCmdEnvAsync, onError: HandleNonFatalCommandException);
        PreviewTransparentProxyCodexCaptureCommand = new AsyncRelayCommand(PreviewTransparentProxyCodexCaptureAsync, CanConfigureTransparentProxyCodexCapture, onError: HandleNonFatalCommandException);
        ApplyTransparentProxyCodexCaptureCommand = new AsyncRelayCommand(ApplyTransparentProxyCodexCaptureAsync, CanConfigureTransparentProxyCodexCapture, onError: HandleNonFatalCommandException);
        RestoreTransparentProxyCodexCaptureCommand = new AsyncRelayCommand(RestoreTransparentProxyCodexCaptureAsync, CanConfigureTransparentProxyCodexCapture, onError: HandleNonFatalCommandException);
        PreviewTransparentProxyCodexLauncherCommand = new AsyncRelayCommand(PreviewTransparentProxyCodexLauncherAsync, CanConfigureTransparentProxyLauncher, onError: HandleNonFatalCommandException);
        WriteTransparentProxyCodexLauncherCommand = new AsyncRelayCommand(WriteTransparentProxyCodexLauncherAsync, CanConfigureTransparentProxyLauncher, onError: HandleNonFatalCommandException);
        PreviewTransparentProxyClaudeCaptureCommand = new AsyncRelayCommand(PreviewTransparentProxyClaudeCaptureAsync, CanConfigureTransparentProxyClaudeCapture, onError: HandleNonFatalCommandException);
        ApplyTransparentProxyClaudeCaptureCommand = new AsyncRelayCommand(ApplyTransparentProxyClaudeCaptureAsync, CanConfigureTransparentProxyClaudeCapture, onError: HandleNonFatalCommandException);
        RestoreTransparentProxyClaudeCaptureCommand = new AsyncRelayCommand(RestoreTransparentProxyClaudeCaptureAsync, CanConfigureTransparentProxyClaudeCapture, onError: HandleNonFatalCommandException);
        PreviewTransparentProxyClaudeLauncherCommand = new AsyncRelayCommand(PreviewTransparentProxyClaudeLauncherAsync, CanConfigureTransparentProxyLauncher, onError: HandleNonFatalCommandException);
        WriteTransparentProxyClaudeLauncherCommand = new AsyncRelayCommand(WriteTransparentProxyClaudeLauncherAsync, CanConfigureTransparentProxyLauncher, onError: HandleNonFatalCommandException);
        PreviewTransparentProxyVsCodeCaptureCommand = new AsyncRelayCommand(PreviewTransparentProxyVsCodeCaptureAsync, CanConfigureTransparentProxyVsCodeCapture, onError: HandleNonFatalCommandException);
        ApplyTransparentProxyVsCodeCaptureCommand = new AsyncRelayCommand(ApplyTransparentProxyVsCodeCaptureAsync, CanConfigureTransparentProxyVsCodeCapture, onError: HandleNonFatalCommandException);
        RestoreTransparentProxyVsCodeCaptureCommand = new AsyncRelayCommand(RestoreTransparentProxyVsCodeCaptureAsync, CanConfigureTransparentProxyVsCodeCapture, onError: HandleNonFatalCommandException);
        ToggleTransparentProxyProviderSettingsCommand = new AsyncRelayCommand(ToggleTransparentProxyProviderSettingsAsync, onError: HandleNonFatalCommandException);
        ToggleTransparentProxyOAuthPanelCommand = new AsyncRelayCommand(ToggleTransparentProxyOAuthPanelAsync, onError: HandleNonFatalCommandException);
        StartCodexOAuthLoginCommand = new AsyncRelayCommand(StartCodexOAuthLoginAsync, () => !IsCodexOAuthLoginInProgress, onError: HandleNonFatalCommandException);
        CancelCodexOAuthLoginCommand = new AsyncRelayCommand(CancelCodexOAuthLoginAsync, () => IsCodexOAuthLoginInProgress, onError: HandleNonFatalCommandException);
        SubmitCodexOAuthCallbackCommand = new AsyncRelayCommand(SubmitCodexOAuthCallbackAsync, () => IsCodexOAuthLoginInProgress, onError: HandleNonFatalCommandException);
        CopyCodexOAuthLoginUrlCommand = new AsyncRelayCommand(CopyCodexOAuthLoginUrlAsync, () => !string.IsNullOrWhiteSpace(CodexOAuthLoginUrlText), onError: HandleNonFatalCommandException);
        ImportCodexOAuthCredentialCommand = new AsyncRelayCommand(ImportCodexOAuthCredentialAsync, onError: HandleNonFatalCommandException);
        RefreshCodexOAuthCredentialCommand = new AsyncRelayCommand<CodexOAuthCredentialViewModel?>(RefreshCodexOAuthCredentialAsync, item => item is not null, onError: HandleNonFatalCommandException);
        DisableCodexOAuthCredentialCommand = new AsyncRelayCommand<CodexOAuthCredentialViewModel?>(DisableCodexOAuthCredentialAsync, item => item is not null, onError: HandleNonFatalCommandException);
        ExportCodexOAuthCredentialCommand = new AsyncRelayCommand<CodexOAuthCredentialViewModel?>(ExportCodexOAuthCredentialAsync, item => item is not null, onError: HandleNonFatalCommandException);
        DeleteCodexOAuthCredentialCommand = new AsyncRelayCommand<CodexOAuthCredentialViewModel?>(DeleteCodexOAuthCredentialAsync, item => item is not null, onError: HandleNonFatalCommandException);
        OpenTransparentProxyRouteSettingsCommand = new AsyncRelayCommand<TransparentProxyRouteEditorItemViewModel?>(OpenTransparentProxyRouteSettingsAsync, item => item is not null, onError: HandleNonFatalCommandException);
        OpenTransparentProxyRuntimeRouteSettingsCommand = new AsyncRelayCommand<TransparentProxyRouteViewModel?>(OpenTransparentProxyRuntimeRouteSettingsAsync, route => route is not null, onError: HandleNonFatalCommandException);
        CloseTransparentProxyRouteSettingsCommand = new AsyncRelayCommand(CloseTransparentProxyRouteSettingsAsync, onError: HandleNonFatalCommandException);
        FetchTransparentProxyRouteEditorItemModelsCommand = new AsyncRelayCommand<TransparentProxyRouteEditorItemViewModel?>(FetchTransparentProxyRouteEditorItemModelsAsync, item => item is not null, onError: HandleNonFatalCommandException);
        ResetTransparentProxyRouteCircuitCommand = new AsyncRelayCommand<TransparentProxyRouteEditorItemViewModel?>(ResetTransparentProxyRouteCircuitAsync, item => item is not null, onError: HandleNonFatalCommandException);
        AddTransparentProxyRouteModelMappingCommand = new AsyncRelayCommand<TransparentProxyRouteEditorItemViewModel?>(AddTransparentProxyRouteModelMappingAsync, item => item is not null, onError: HandleNonFatalCommandException);
        RemoveTransparentProxyRouteModelMappingCommand = new AsyncRelayCommand<TransparentProxyModelMappingViewModel?>(RemoveTransparentProxyRouteModelMappingAsync, item => item is not null, onError: HandleNonFatalCommandException);
        RemoveTransparentProxyRouteEditorItemCommand = new AsyncRelayCommand(RemoveTransparentProxyRouteEditorItemAsync, () => SelectedTransparentProxyRouteEditorItem is not null, onError: HandleNonFatalCommandException);
        MoveTransparentProxyRouteEditorItemUpCommand = new AsyncRelayCommand(MoveTransparentProxyRouteEditorItemUpAsync, () => SelectedTransparentProxyRouteEditorItem is not null, onError: HandleNonFatalCommandException);
        MoveTransparentProxyRouteEditorItemDownCommand = new AsyncRelayCommand(MoveTransparentProxyRouteEditorItemDownAsync, () => SelectedTransparentProxyRouteEditorItem is not null, onError: HandleNonFatalCommandException);
        CopyTransparentProxyEndpointCommand = new AsyncRelayCommand(CopyTransparentProxyEndpointAsync, onError: HandleNonFatalCommandException);
        TestTransparentProxyHealthCommand = new AsyncRelayCommand(TestTransparentProxyHealthAsync, onError: HandleNonFatalCommandException);
        ClearTransparentProxyLogsCommand = new AsyncRelayCommand(ClearTransparentProxyLogsAsync);
        ExportTransparentProxyLogsCommand = new AsyncRelayCommand(ExportTransparentProxyLogsAsync, onError: HandleNonFatalCommandException);
        CloseTransparentProxyLogDetailCommand = new AsyncRelayCommand(CloseTransparentProxyLogDetailAsync);
        ClearTransparentProxyCacheCommand = new AsyncRelayCommand(ClearTransparentProxyCacheAsync);
        ToggleTransparentProxyLogExpandedCommand = new AsyncRelayCommand(ToggleTransparentProxyLogExpandedAsync);
        RunIpRiskReviewCommand = new AsyncRelayCommand(RunIpRiskReviewWithGlobalProgressAsync, CanRun);
        ConfirmConfirmationDialogCommand = new AsyncRelayCommand(ConfirmConfirmationDialogAsync);
        CancelConfirmationDialogCommand = new AsyncRelayCommand(CancelConfirmationDialogAsync);
        SelectAllClientApplyTargetsCommand = new AsyncRelayCommand(SelectAllClientApplyTargetsAsync, CanEditClientApplyTargetSelection);
        InvertClientApplyTargetsCommand = new AsyncRelayCommand(InvertClientApplyTargetsAsync, CanEditClientApplyTargetSelection);
        ToggleClientApplyTargetSelectionCommand = new AsyncRelayCommand<ClientApplyTargetItemViewModel?>(ToggleClientApplyTargetSelectionAsync);
        ConfirmClientApplyTargetDialogCommand = new AsyncRelayCommand(ConfirmClientApplyTargetDialogAsync, CanConfirmClientApplyTargetDialog);
        CancelClientApplyTargetDialogCommand = new AsyncRelayCommand(CancelClientApplyTargetDialogAsync);
        OpenCodexConfigTemplateDialogCommand = new AsyncRelayCommand<ClientApplyTargetItemViewModel?>(OpenCodexConfigTemplateDialogAsync, item => item?.HasSettings == true);
        SaveCodexConfigTemplateCommand = new AsyncRelayCommand(SaveCodexConfigTemplateDialogAsync, () => IsCodexConfigTemplateDialogOpen);
        ResetCodexConfigTemplateCommand = new AsyncRelayCommand(ResetCodexConfigTemplateDialogAsync, () => IsCodexConfigTemplateDialogOpen);
        CloseCodexConfigTemplateDialogCommand = new AsyncRelayCommand(CloseCodexConfigTemplateDialogAsync);
        OpenAboutDialogCommand = new AsyncRelayCommand(OpenAboutDialogAsync);
        CloseAboutDialogCommand = new AsyncRelayCommand(CloseAboutDialogAsync);
        OpenProjectHomepageCommand = new AsyncRelayCommand(OpenProjectHomepageAsync);
        ToggleNavigationRailCommand = new AsyncRelayCommand(ToggleNavigationRailAsync);
        SendChatMessageCommand = new AsyncRelayCommand(SendChatMessageAsync, CanSendChatMessage, onError: HandleNonFatalCommandException);
        StopChatStreamingCommand = new AsyncRelayCommand(StopChatStreamingAsync, CanStopChatStreaming);
        ClearChatSessionCommand = new AsyncRelayCommand(ClearChatSessionAsync, onError: HandleNonFatalCommandException);
        NewChatSessionCommand = new AsyncRelayCommand(NewChatSessionAsync, CanEditChatAttachments, onError: HandleNonFatalCommandException);
        DeleteChatSessionCommand = new AsyncRelayCommand(DeleteChatSessionAsync, () => CanEditChatAttachments() && ChatSessions.Count > 0, onError: HandleNonFatalCommandException);
        BeginRenameChatSessionCommand = new AsyncRelayCommand<ChatSessionListItemViewModel?>(BeginRenameChatSessionAsync, item => item is not null && CanEditChatAttachments(), onError: HandleNonFatalCommandException);
        CommitRenameChatSessionCommand = new AsyncRelayCommand<ChatSessionListItemViewModel?>(CommitRenameChatSessionAsync, item => item is not null && CanEditChatAttachments(), onError: HandleNonFatalCommandException);
        CancelRenameChatSessionCommand = new AsyncRelayCommand<ChatSessionListItemViewModel?>(CancelRenameChatSessionAsync, item => item is not null, onError: HandleNonFatalCommandException);
        RegenerateLastChatAnswerCommand = new AsyncRelayCommand(RegenerateLastChatAnswerAsync, CanRegenerateLastChatAnswer, onError: HandleNonFatalCommandException);
        ExportChatSessionMarkdownCommand = new AsyncRelayCommand(ExportChatSessionMarkdownAsync, CanExportChatSession, onError: HandleNonFatalCommandException);
        ExportChatSessionTextCommand = new AsyncRelayCommand(ExportChatSessionTextAsync, CanExportChatSession, onError: HandleNonFatalCommandException);
        AddChatImageAttachmentCommand = new AsyncRelayCommand(AddChatImageAttachmentAsync, CanEditChatAttachments, onError: HandleNonFatalCommandException);
        AddChatTextFileAttachmentCommand = new AsyncRelayCommand(AddChatTextFileAttachmentAsync, CanEditChatAttachments, onError: HandleNonFatalCommandException);
        AddChatAttachmentFilesCommand = new AsyncRelayCommand<string[]?>(AddChatAttachmentFilesAsync, files => files is { Length: > 0 } && CanEditChatAttachments(), onError: HandleNonFatalCommandException);
        ToggleChatSettingsPanelCommand = new AsyncRelayCommand(ToggleChatSettingsPanelAsync, onError: HandleNonFatalCommandException);
        CloseChatSettingsPanelCommand = new AsyncRelayCommand(CloseChatSettingsPanelAsync, onError: HandleNonFatalCommandException);
        AddChatSelectedModelCommand = new AsyncRelayCommand(AddChatSelectedModelAsync, CanEditChatAttachments, onError: HandleNonFatalCommandException);
        ClearChatSelectedModelsCommand = new AsyncRelayCommand(ClearChatSelectedModelsAsync, CanEditChatAttachments, onError: HandleNonFatalCommandException);
        RemoveChatSelectedModelCommand = new AsyncRelayCommand<ChatModelSelectionViewModel?>(RemoveChatSelectedModelAsync, model => model is not null && CanEditChatAttachments(), onError: HandleNonFatalCommandException);
        RemoveChatAttachmentCommand = new AsyncRelayCommand<ChatAttachmentViewModel?>(RemoveChatAttachmentAsync, attachment => attachment is not null && CanEditChatAttachments(), onError: HandleNonFatalCommandException);
        EditChatMessageCommand = new AsyncRelayCommand<ChatMessageViewModel?>(EditChatMessageAsync, message => message?.IsUser == true && CanEditChatAttachments(), onError: HandleNonFatalCommandException);
        CancelChatEditCommand = new AsyncRelayCommand(CancelChatEditAsync, () => IsEditingChatMessage, onError: HandleNonFatalCommandException);
        ApplyChatPresetCommand = new AsyncRelayCommand(ApplyChatPresetAsync, () => SelectedChatPreset is not null && CanEditChatAttachments(), onError: HandleNonFatalCommandException);
        SaveChatPresetCommand = new AsyncRelayCommand(SaveChatPresetAsync, CanEditChatAttachments, onError: HandleNonFatalCommandException);
        DeleteChatPresetCommand = new AsyncRelayCommand(DeleteChatPresetAsync, () => SelectedChatPreset?.IsBuiltIn == false && CanEditChatAttachments(), onError: HandleNonFatalCommandException);
        CopyChatCodeBlockCommand = new AsyncRelayCommand<ChatContentBlockViewModel?>(CopyChatCodeBlockAsync, block => block?.IsCode == true, onError: HandleNonFatalCommandException);
        CopyChatMessageCommand = new AsyncRelayCommand<ChatMessageViewModel?>(CopyChatMessageAsync, message => message?.CanCopy == true, onError: HandleNonFatalCommandException);
        CopyChatModelAnswerCommand = new AsyncRelayCommand<ChatModelAnswerViewModel?>(CopyChatModelAnswerAsync, answer => answer?.CanCopy == true, onError: HandleNonFatalCommandException);

        ResetIpRiskPresentation();
        LoadState();
        StartTransparentProxyUnifiedEndpointOnLaunch();
        _ = LoadTransparentProxyLogsAsync();
        LoadChatSession();
        RefreshSingleStationInlineChartPlaceholder();
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
