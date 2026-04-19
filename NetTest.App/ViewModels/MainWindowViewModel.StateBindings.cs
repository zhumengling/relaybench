using NetTest.App.Infrastructure;
using NetTest.App.Services;

namespace NetTest.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RunQuickSuiteCommand.RaiseCanExecuteChanged();
                ExportCurrentReportCommand.RaiseCanExecuteChanged();
                RunNetworkCommand.RaiseCanExecuteChanged();
                RunChatGptTraceCommand.RaiseCanExecuteChanged();
                RunStunCommand.RaiseCanExecuteChanged();
                FetchProxyModelsCommand.RaiseCanExecuteChanged();
                FetchProxyBatchSharedModelsCommand.RaiseCanExecuteChanged();
                FetchProxyBatchEntryModelsCommand.RaiseCanExecuteChanged();
                AddProxyBatchTemplateRowCommand.RaiseCanExecuteChanged();
                PasteProxyBatchTemplateRowsCommand.RaiseCanExecuteChanged();
                ApplyProxyBatchTemplateDefaultsCommand.RaiseCanExecuteChanged();
                ClearProxyBatchTemplateEmptyRowsCommand.RaiseCanExecuteChanged();
                FetchProxyBatchTemplateRowModelsCommand.RaiseCanExecuteChanged();
                OpenProxyBatchEditorCommand.RaiseCanExecuteChanged();
                RunProxyCommand.RaiseCanExecuteChanged();
                RunProxyDeepCommand.RaiseCanExecuteChanged();
                RunProxySeriesCommand.RaiseCanExecuteChanged();
                RunSelectedSingleStationModeCommand.RaiseCanExecuteChanged();
                RunProxyBatchCommand.RaiseCanExecuteChanged();
                RunSelectedBatchDeepTestsCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(CanRunSelectedBatchDeepTests));
                StopCurrentProxyTestCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(CanStopCurrentProxyTest));
                RetryProxyChartCommand.RaiseCanExecuteChanged();
                ToggleProxyChartViewCommand.RaiseCanExecuteChanged();
                ToggleProxyChartImageOnlyModeCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(CanToggleProxyChartView));
                OnPropertyChanged(nameof(CanToggleProxyChartImageOnlyMode));
                RunSpeedTestCommand.RaiseCanExecuteChanged();
                RunRouteCommand.RaiseCanExecuteChanged();
                RunRouteContinuousCommand.RaiseCanExecuteChanged();
                StopRouteContinuousCommand.RaiseCanExecuteChanged();
                DetectPortScanEngineCommand.RaiseCanExecuteChanged();
                RunPortScanCommand.RaiseCanExecuteChanged();
                RunPortScanBatchCommand.RaiseCanExecuteChanged();
                ExportPortScanCsvCommand.RaiseCanExecuteChanged();
                ExportPortScanExcelCommand.RaiseCanExecuteChanged();
                RunSplitRoutingCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (SetProperty(ref _statusMessage, value))
            {
                AppendLiveStatus(value);
            }
        }
    }

    public string LastRunAt
    {
        get => _lastRunAt;
        private set => SetProperty(ref _lastRunAt, value);
    }

    public string NetworkSummary
    {
        get => _networkSummary;
        private set => SetProperty(ref _networkSummary, value);
    }

    public string AdapterSummary
    {
        get => _adapterSummary;
        private set => SetProperty(ref _adapterSummary, value);
    }

    public string PingSummary
    {
        get => _pingSummary;
        private set => SetProperty(ref _pingSummary, value);
    }

    public string ChatGptSummary
    {
        get => _chatGptSummary;
        private set => SetProperty(ref _chatGptSummary, value);
    }

    public string ChatGptRawTrace
    {
        get => _chatGptRawTrace;
        private set => SetProperty(ref _chatGptRawTrace, value);
    }

    public string SelectedStunTransportKey
    {
        get => _selectedStunTransportKey;
        set
        {
            var normalized = StunServerPresetCatalog.ResolveTransportKey(value, StunServer);
            if (SetProperty(ref _selectedStunTransportKey, normalized))
            {
                RefreshStunServerOptions(syncCurrentHost: true);
            }
        }
    }

    public string StunServer
    {
        get => _stunServer;
        set => SetProperty(ref _stunServer, NormalizeStunServerHost(value));
    }

    public string StunSummary
    {
        get => _stunSummary;
        private set => SetProperty(ref _stunSummary, value);
    }

    public string StunAttributeSummary
    {
        get => _stunAttributeSummary;
        private set => SetProperty(ref _stunAttributeSummary, value);
    }

    public string ProxyBaseUrl
    {
        get => _proxyBaseUrl;
        set
        {
            if (SetProperty(ref _proxyBaseUrl, value))
            {
                RefreshProxyTrendView(value);
            }
        }
    }

    public string ProxyApiKey
    {
        get => _proxyApiKey;
        set => SetProperty(ref _proxyApiKey, value);
    }

    public string ProxyModel
    {
        get => _proxyModel;
        set
        {
            if (SetProperty(ref _proxyModel, value))
            {
                if (_proxyModelPickerTarget == ProxyModelPickerTarget.DefaultModel)
                {
                    SyncSelectedProxyCatalogModel(value);
                }
            }
        }
    }

    public string ProxyTimeoutSecondsText
    {
        get => _proxyTimeoutSecondsText;
        set => SetProperty(ref _proxyTimeoutSecondsText, value);
    }

    public bool ProxyIgnoreTlsErrors
    {
        get => _proxyIgnoreTlsErrors;
        set => SetProperty(ref _proxyIgnoreTlsErrors, value);
    }

    public string ProxySeriesRoundsText
    {
        get => _proxySeriesRoundsText;
        set => SetProperty(ref _proxySeriesRoundsText, value);
    }

    public string ProxySeriesDelayMsText
    {
        get => _proxySeriesDelayMsText;
        set => SetProperty(ref _proxySeriesDelayMsText, value);
    }

    public bool IsProxyBatchEditorOpen
    {
        get => _isProxyBatchEditorOpen;
        private set => SetProperty(ref _isProxyBatchEditorOpen, value);
    }

    public bool IsProxyModelPickerOpen
    {
        get => _isProxyModelPickerOpen;
        private set => SetProperty(ref _isProxyModelPickerOpen, value);
    }

    public bool IsProxyTrendChartOpen
    {
        get => _isProxyTrendChartOpen;
        private set => SetProperty(ref _isProxyTrendChartOpen, value);
    }

    public bool HasProxyChartRetryAction
        => _proxyChartRetryMode is not ProxyChartRetryMode.None;

    public bool CanStopCurrentProxyTest
        => IsBusy &&
           _currentProxyOperationCancellationSource is { IsCancellationRequested: false };

    public string ProxyChartRetryButtonText
    {
        get => _proxyChartRetryButtonText;
        private set => SetProperty(ref _proxyChartRetryButtonText, value);
    }

    public bool IsProxyChartImageOnlyMode
    {
        get => _isProxyChartImageOnlyMode;
        private set => SetProperty(ref _isProxyChartImageOnlyMode, value);
    }

    public bool CanToggleProxyChartImageOnlyMode
        => !IsBusy && HasProxyChartDialogImage;

    public string ProxyChartImageOnlyModeButtonText
        => IsProxyChartImageOnlyMode ? "恢复完整布局" : "仅看图表";

    public string ProxyModelPickerTargetSummary
        => $"当前回填位置：{GetProxyModelPickerTargetDisplayName()}";

    public string ProxyModelPickerInstruction
        => $"点击左侧任一模型后，会自动回填到“{GetProxyModelPickerTargetDisplayName()}”并关闭弹窗。";

    public string ProxyModelCatalogFilterText
    {
        get => _proxyModelCatalogFilterText;
        set
        {
            if (SetProperty(ref _proxyModelCatalogFilterText, value))
            {
                RefreshVisibleProxyCatalogModels();
                SyncSelectedProxyCatalogModel(GetCurrentProxyModelPickerValue());
            }
        }
    }

    public string ProxyModelCatalogSummary
    {
        get => _proxyModelCatalogSummary;
        private set => SetProperty(ref _proxyModelCatalogSummary, value);
    }

    public string ProxyModelCatalogDetail
    {
        get => _proxyModelCatalogDetail;
        private set => SetProperty(ref _proxyModelCatalogDetail, value);
    }

    public string? SelectedProxyCatalogModel
    {
        get => _selectedProxyCatalogModel;
        set
        {
            if (SetProperty(ref _selectedProxyCatalogModel, value) &&
                !_suppressProxyCatalogSelectionApply &&
                !string.IsNullOrWhiteSpace(value))
            {
                ApplyProxyCatalogSelection(value);
                IsProxyModelPickerOpen = false;
            }
        }
    }

    public string ProxySummary
    {
        get => _proxySummary;
        private set
        {
            if (SetProperty(ref _proxySummary, value))
            {
                OnPropertyChanged(nameof(SingleStationResultSummary));
            }
        }
    }

    public string ProxyDetail
    {
        get => _proxyDetail;
        private set
        {
            if (SetProperty(ref _proxyDetail, value))
            {
                OnPropertyChanged(nameof(SingleStationResultDetail));
            }
        }
    }

    public string ProxyStabilitySummary
    {
        get => _proxyStabilitySummary;
        private set
        {
            if (SetProperty(ref _proxyStabilitySummary, value))
            {
                OnPropertyChanged(nameof(SingleStationResultSummary));
            }
        }
    }

    public string ProxyStabilityDetail
    {
        get => _proxyStabilityDetail;
        private set
        {
            if (SetProperty(ref _proxyStabilityDetail, value))
            {
                OnPropertyChanged(nameof(SingleStationResultDetail));
            }
        }
    }

    public string HistorySummary
    {
        get => _historySummary;
        private set => SetProperty(ref _historySummary, value);
    }

    public string PlannedModulesSummary =>
        "当前已实现：基础网络快照、官方 API 可用性检测、STUN 探测、兼容 OpenAI 的中转站模型列表拉取、单次诊断、稳定性序列、入口组对比与 CDN / 边缘观察、Cloudflare 风格测速、类 MTR 路由追踪、真实地图可视化、参考 ip.skk.moe 的 IP / 分流诊断、内置端口扫描引擎与安全模板、批量目标扫描、结果筛选、CSV / Excel 导出、本地结果持久化、结构化报告导出，以及一键启动与 .NET 运行时检查。下一阶段可继续增强更丰富的解锁目录、更完整的 NAT 类型归类，以及更强的结构化报告能力。";
}
