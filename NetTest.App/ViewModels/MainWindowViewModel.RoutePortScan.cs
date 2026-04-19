using System.Collections.ObjectModel;
using System.Windows.Media.Imaging;
using NetTest.App.Infrastructure;
using NetTest.App.Services;
using NetTest.Core.Models;

namespace NetTest.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private const int MaxRouteLiveRawCharacters = 60_000;
    private const int MaxPortScanLogCharacters = 60_000;
    private readonly Dictionary<int, string> _routeLiveHopPreviewLines = [];
    private readonly HashSet<string> _portScanLiveFindingKeys = [];
    private readonly Dictionary<string, PortScanResult> _portScanBatchResultLookup = new(StringComparer.OrdinalIgnoreCase);
    private readonly PortScanExportService _portScanExportService = new();
    private readonly List<PortScanResult> _lastPortScanBatchResults = [];
    private bool _isRouteLiveExecutionActive;
    private DateTimeOffset? _routeLiveStartedAt;
    private int _routeLiveRawLineCount;
    private string _routeLiveEngineHint = "等待探测引擎选择";
    private string _routeLiveLastProgressMessage = "等待开始";
    private readonly RouteMapRenderService _routeMapRenderService = new();
    private string _routeTarget = "chatgpt.com";
    private string _selectedRouteResolverKey = "auto";
    private string _routeMaxHopsText = "20";
    private string _routeTimeoutMsText = "900";
    private string _routeSamplesPerHopText = "3";
    private string _routeContinuousDurationSecondsText = "60";
    private string _routeContinuousIntervalMsText = "500";
    private string _routeSummary = "运行路由检测后，这里会显示内置 ICMP MTR / Windows tracert 的摘要和逐跳采样结果。";
    private string _routeHopSummary = "暂无 hop 列表。";
    private string _routeRawOutput = "暂无原始追踪输出。";
    private string _routeMapSummary = "运行路由检测后，这里会生成真实路由路径的地理地图。";
    private string _routeGeoSummary = "暂无 hop 地理定位结果。";
    private BitmapSource? _routeMapImage;
    private bool _isRouteContinuousExecutionActive;
    private int _routeContinuousCurrentRound;
    private int _routeContinuousPlannedDurationSeconds;
    private DateTimeOffset? _routeContinuousEndsAt;
    private CancellationTokenSource? _routeContinuousCancellationSource;
    private readonly Dictionary<int, RouteContinuousHopAggregate> _routeContinuousHopAggregates = [];
    private string _portScanTarget = string.Empty;
    private string _selectedPortScanProfileKey = "relay-baseline";
    private string _portScanCustomPortsText = string.Empty;
    private string _portScanBatchTargetsText = string.Empty;
    private string _portScanBatchConcurrencyText = "3";
    private string _portScanSearchText = string.Empty;
    private string _selectedPortScanProtocolFilterKey = "all";
    private string _portScanSummary = "可先检测内置端口扫描引擎，再对目标执行本地异步 TCP / UDP 轻量扫描。";
    private string _portScanDetail = "暂无端口扫描结果。";
    private string _portScanRawOutput = "尚未捕获扫描日志输出。";
    private string _portScanProgressSummary = "尚未开始扫描。";
    private string _portScanFilterSummary = "当前显示 0 / 0 条结果。";
    private string _portScanBatchSummary = "可在这里粘贴多个目标，逐行执行批量端口扫描。";
    private string _portScanExportSummary = "暂无可导出结果。";
    private string _portScanCurrentExecutionTarget = string.Empty;
    private PortScanBatchRowViewModel? _selectedPortScanBatchRow;
    private bool _portScanBatchSelectionChangeFromCode;
    private bool _portScanBatchManualSelectionActive;
    private double _portScanProgressValue;
    private double _portScanProgressMaximum = 1d;

    public ObservableCollection<SelectionOption> RouteResolverOptions { get; } =
    [
        new("auto", "自动（系统优先 / DoH 回退）"),
        new("system", "系统 DNS"),
        new("google-doh", "Google DoH"),
        new("cloudflare-doh", "Cloudflare DoH")
    ];

    public ObservableCollection<PortScanProfile> PortScanProfiles { get; }

    public ObservableCollection<PortScanFinding> PortScanFindings { get; } = [];

    public ObservableCollection<PortScanFinding> FilteredPortScanFindings { get; } = [];

    public ObservableCollection<PortScanBatchRowViewModel> PortScanBatchRows { get; } = [];

    public ObservableCollection<SelectionOption> PortScanProtocolFilterOptions { get; } =
    [
        new("all", "全部协议"),
        new("tcp", "仅 TCP"),
        new("udp", "仅 UDP")
    ];

    public string RouteTarget
    {
        get => _routeTarget;
        set => SetProperty(ref _routeTarget, value);
    }

    public string SelectedRouteResolverKey
    {
        get => _selectedRouteResolverKey;
        set
        {
            if (SetProperty(ref _selectedRouteResolverKey, ResolveRouteResolverKey(value)))
            {
                OnPropertyChanged(nameof(SelectedRouteResolverDescription));
            }
        }
    }

    public string RouteMaxHopsText
    {
        get => _routeMaxHopsText;
        set => SetProperty(ref _routeMaxHopsText, value);
    }

    public string RouteTimeoutMsText
    {
        get => _routeTimeoutMsText;
        set => SetProperty(ref _routeTimeoutMsText, value);
    }

    public string RouteSamplesPerHopText
    {
        get => _routeSamplesPerHopText;
        set => SetProperty(ref _routeSamplesPerHopText, value);
    }

    public string RouteContinuousDurationSecondsText
    {
        get => _routeContinuousDurationSecondsText;
        set => SetProperty(ref _routeContinuousDurationSecondsText, value);
    }

    public string RouteContinuousIntervalMsText
    {
        get => _routeContinuousIntervalMsText;
        set => SetProperty(ref _routeContinuousIntervalMsText, value);
    }

    public string RouteSummary
    {
        get => _routeSummary;
        private set => SetProperty(ref _routeSummary, value);
    }

    public string RouteHopSummary
    {
        get => _routeHopSummary;
        private set => SetProperty(ref _routeHopSummary, value);
    }

    public string RouteRawOutput
    {
        get => _routeRawOutput;
        private set => SetProperty(ref _routeRawOutput, value);
    }

    public string RouteMapSummary
    {
        get => _routeMapSummary;
        private set => SetProperty(ref _routeMapSummary, value);
    }

    public string RouteGeoSummary
    {
        get => _routeGeoSummary;
        private set => SetProperty(ref _routeGeoSummary, value);
    }

    public BitmapSource? RouteMapImage
    {
        get => _routeMapImage;
        private set => SetProperty(ref _routeMapImage, value);
    }

    public string SelectedRouteResolverDescription
    {
        get
        {
            var option = GetSelectedRouteResolverOption();
            return option?.DisplayName ?? "自动（系统优先 / DoH 回退）";
        }
    }

    public string PortScanTarget
    {
        get => _portScanTarget;
        set => SetProperty(ref _portScanTarget, value);
    }

    public string SelectedPortScanProfileKey
    {
        get => _selectedPortScanProfileKey;
        set
        {
            if (SetProperty(ref _selectedPortScanProfileKey, ResolvePortScanProfileKey(value)))
            {
                OnPropertyChanged(nameof(SelectedPortScanProfileDescription));
            }
        }
    }

    public string PortScanCustomPortsText
    {
        get => _portScanCustomPortsText;
        set
        {
            if (SetProperty(ref _portScanCustomPortsText, value))
            {
                OnPropertyChanged(nameof(SelectedPortScanProfileDescription));
            }
        }
    }

    public string PortScanBatchTargetsText
    {
        get => _portScanBatchTargetsText;
        set
        {
            if (SetProperty(ref _portScanBatchTargetsText, value))
            {
                RefreshPortScanBatchSummary();
            }
        }
    }

    public string PortScanBatchConcurrencyText
    {
        get => _portScanBatchConcurrencyText;
        set
        {
            if (SetProperty(ref _portScanBatchConcurrencyText, value))
            {
                RefreshPortScanBatchSummary();
            }
        }
    }

    public string PortScanSearchText
    {
        get => _portScanSearchText;
        set
        {
            if (SetProperty(ref _portScanSearchText, value))
            {
                RefreshFilteredPortScanFindings();
            }
        }
    }

    public string SelectedPortScanProtocolFilterKey
    {
        get => _selectedPortScanProtocolFilterKey;
        set
        {
            if (SetProperty(ref _selectedPortScanProtocolFilterKey, ResolvePortScanProtocolFilterKey(value)))
            {
                RefreshFilteredPortScanFindings();
            }
        }
    }

    public string SelectedPortScanProfileDescription
    {
        get
        {
            var profile = GetSelectedPortScanProfile();
            if (profile is null)
            {
                return "请选择扫描模板。";
            }

            var portSummary = string.IsNullOrWhiteSpace(PortScanCustomPortsText)
                ? $"默认端口：{profile.PortListText}"
                : $"自定义端口：{PortScanCustomPortsText.Trim()}";

            return $"{profile.Description} {portSummary}；传输：{profile.TransportSummaryText}；超时：{profile.ConnectTimeoutMilliseconds} ms；并发：{profile.MaxConcurrency}；探测：{profile.ProbeSummaryText}";
        }
    }

    public string PortScanSummary
    {
        get => _portScanSummary;
        private set => SetProperty(ref _portScanSummary, value);
    }

    public string PortScanDetail
    {
        get => _portScanDetail;
        private set => SetProperty(ref _portScanDetail, value);
    }

    public string PortScanRawOutput
    {
        get => _portScanRawOutput;
        private set => SetProperty(ref _portScanRawOutput, value);
    }

    public string PortScanProgressSummary
    {
        get => _portScanProgressSummary;
        private set => SetProperty(ref _portScanProgressSummary, value);
    }

    public string PortScanFilterSummary
    {
        get => _portScanFilterSummary;
        private set => SetProperty(ref _portScanFilterSummary, value);
    }

    public string PortScanBatchSummary
    {
        get => _portScanBatchSummary;
        private set => SetProperty(ref _portScanBatchSummary, value);
    }

    public string PortScanExportSummary
    {
        get => _portScanExportSummary;
        private set => SetProperty(ref _portScanExportSummary, value);
    }

    public PortScanBatchRowViewModel? SelectedPortScanBatchRow
    {
        get => _selectedPortScanBatchRow;
        set
        {
            if (SetProperty(ref _selectedPortScanBatchRow, value))
            {
                if (!_portScanBatchSelectionChangeFromCode)
                {
                    _portScanBatchManualSelectionActive = value is not null;
                }

                ShowSelectedPortScanBatchRowDetails(value);
            }
        }
    }

    public double PortScanProgressValue
    {
        get => _portScanProgressValue;
        private set => SetProperty(ref _portScanProgressValue, value);
    }

    public double PortScanProgressMaximum
    {
        get => _portScanProgressMaximum;
        private set => SetProperty(ref _portScanProgressMaximum, value);
    }

    private Task RunRouteAsync()
        => ExecuteBusyActionAsync("正在运行内置 MTR / tracert 路由探测...", RunRouteCoreAsync);

    private Task RunRouteContinuousAsync()
        => ExecuteBusyActionAsync("正在按设定时长持续运行路由 / MTR...", RunRouteContinuousCoreAsync);

    private Task StopRouteContinuousAsync()
    {
        if (_routeContinuousCancellationSource is { IsCancellationRequested: false })
        {
            _routeContinuousCancellationSource.Cancel();
            StatusMessage = "已请求停止持续运行，将在当前探测步骤结束后停止。";
            DashboardCards[5].Status = "停止中";
            DashboardCards[5].Detail = "持续运行停止请求已发送。";
        }

        RefreshRouteContinuousCommandStates();
        return Task.CompletedTask;
    }

    private Task DetectPortScanEngineAsync()
        => ExecuteBusyActionAsync("正在检测本地端口扫描引擎...", DetectPortScanEngineCoreAsync);

    private Task RunPortScanAsync()
        => ExecuteBusyActionAsync("正在运行本地端口扫描...", RunPortScanCoreAsync);

    private Task RunPortScanBatchAsync()
        => ExecuteBusyActionAsync("正在运行批量端口扫描...", RunPortScanBatchCoreAsync);

    private Task ExportPortScanCsvAsync()
        => ExecuteBusyActionAsync("正在导出端口扫描 CSV...", ExportPortScanCsvCoreAsync);

    private Task ExportPortScanExcelAsync()
        => ExecuteBusyActionAsync("正在导出端口扫描 Excel...", ExportPortScanExcelCoreAsync);
}
