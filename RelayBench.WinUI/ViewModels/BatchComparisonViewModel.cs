using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Security.Cryptography;
using System.Text;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using RelayBench.Core.Models;
using RelayBench.Core.Services;
using RelayBench.WinUI.Charts;
using RelayBench.WinUI.Storage;

namespace RelayBench.WinUI.ViewModels;

public enum BatchHeatmapCellTone
{
    Empty,
    Healthy,
    Warning,
    Danger
}

/// <summary>
/// Batch comparison VM. Runs ProxyDiagnosticsService against multiple endpoints sequentially.
/// </summary>
public sealed partial class BatchComparisonViewModel : ObservableObject
{
    private const int BaselineScenarioCount = 6;
    private const int ThroughputBenchmarkStepCount = 1;
    private const int DeepStabilityRounds = 5;
    private const int MaxTimelineItems = 28;
    private const int MaxSiteTimelineItems = 24;
    private static readonly ProxyProbeScenarioKind[] DeepBaselineCapabilityKinds =
    [
        ProxyProbeScenarioKind.Models,
        ProxyProbeScenarioKind.ChatCompletions,
        ProxyProbeScenarioKind.ChatCompletionsStream,
        ProxyProbeScenarioKind.Responses,
        ProxyProbeScenarioKind.StructuredOutput
    ];
    private static readonly (ProxyProbeScenarioKind Kind, string Label)[] DeepSupplementalScenarioBadges =
    [
        (ProxyProbeScenarioKind.SystemPromptMapping, "Sys"),
        (ProxyProbeScenarioKind.FunctionCalling, "Fn"),
        (ProxyProbeScenarioKind.ErrorTransparency, "Err"),
        (ProxyProbeScenarioKind.StreamingIntegrity, "Str"),
        (ProxyProbeScenarioKind.MultiModal, "MM"),
        (ProxyProbeScenarioKind.CacheMechanism, "Cch"),
        (ProxyProbeScenarioKind.InstructionFollowing, "IF"),
        (ProxyProbeScenarioKind.DataExtraction, "DE"),
        (ProxyProbeScenarioKind.StructuredOutputEdge, "SO"),
        (ProxyProbeScenarioKind.ToolCallDeep, "TC"),
        (ProxyProbeScenarioKind.CodeBlockDiscipline, "CB")
    ];
    private static readonly ProxyProbeScenarioKind[] DeepSupplementalScenarioKinds =
        DeepSupplementalScenarioBadges.Select(static item => item.Kind).ToArray();
    private static int QuickPlannedStepCount => BaselineScenarioCount + ThroughputBenchmarkStepCount;
    private static int DeepSupplementalScenarioCount => DeepSupplementalScenarioKinds.Length;
    private static int DeepCapabilityStepCount => QuickPlannedStepCount + DeepSupplementalScenarioCount;
    private static int DeepPlannedStepCount => DeepCapabilityStepCount + DeepStabilityRounds;

    private readonly ProxyDiagnosticsService _diagnosticsService = new();
    private readonly ProxyEndpointModelCacheService _modelCacheService = new();
    private readonly IHistoryRepository _historyRepository = new HistoryRepository();
    private readonly IRouteRepository _routeRepository;
    private readonly Func<CancellationToken, Task<IReadOnlyList<EndpointHistoryItem>>> _endpointHistoryLoader;
    private readonly Func<SharedEndpointState?> _sharedEndpointLoader;
    private readonly Func<string, string, string, Task> _sharedEndpointSaver;
    private readonly Func<ProxyEndpointSettings, CancellationToken, Task<ProxyModelCatalogResult>> _modelCatalogFetcher;
    private readonly Func<ProxyEndpointSettings, ProxyModelCatalogResult, CancellationToken, Task> _modelCatalogCacheSaver;
    private readonly Func<string, string, string, List<string>, Task> _modelHistoryRecorder;
    private readonly Func<ProxyEndpointSettings, CancellationToken, Task<IReadOnlyList<CachedProxyEndpointModelInfo>>> _cachedModelsLoader;
    private readonly Func<ProxyEndpointSettings, IReadOnlyList<string>, IProgress<ModelProtocolProbeProgress>?, CancellationToken, Task<IReadOnlyList<ModelProtocolInfo>>> _modelProtocolDetector;
    private readonly Func<ProxyEndpointSettings, IProgress<ProxyDiagnosticsLiveProgress>?, CancellationToken, Task<ProxyDiagnosticsResult>> _diagnosticsRunner;
    private readonly Func<ProxyEndpointSettings, int, int, IProgress<string>?, IProgress<ProxyDiagnosticsResult>?, CancellationToken, Task<ProxyStabilityResult>> _stabilityRunner;
    private readonly Func<ProxyEndpointSettings, IReadOnlyList<int>, IProgress<ProxyConcurrencyPressureStageResult>?, CancellationToken, Task<ProxyConcurrencyPressureResult>> _concurrencyRunner;
    private readonly Func<ProxyEndpointSettings, ProxyDiagnosticsResult, IProgress<ProxyThroughputBenchmarkLiveProgress>?, CancellationToken, Task<ProxyThroughputBenchmarkResult?>> _throughputBenchmarkRunner;
    private readonly Func<ProxyEndpointSettings, ProxyDiagnosticsResult, IProgress<ProxyDiagnosticsLiveProgress>?, CancellationToken, Task<ProxyDiagnosticsResult>> _supplementalScenarioRunner;
    private readonly bool _modelProtocolDetectionEnabled;
    private CancellationTokenSource? _cts;
    private readonly object _queueProgressGate = new();
    private IReadOnlyList<BatchCompositeTrendSnapshot> _historicalBatchTrend = [BatchCompositeTrendSnapshot.Zero];
    private string? _lastTimelineKey;

    [ObservableProperty] public partial bool IsRunning { get; set; }
    [ObservableProperty] public partial int TotalSites { get; set; }
    [ObservableProperty] public partial int CompletedSites { get; set; }
    [ObservableProperty] public partial double Progress { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; } = "Ready";
    [ObservableProperty] public partial string EstimatedRemaining { get; set; } = "0s";
    [ObservableProperty] public partial string TopSiteName { get; set; } = "0";
    [ObservableProperty] public partial double TopSiteScore { get; set; }
    [ObservableProperty] public partial string TopLatencyDisplay { get; set; } = "0 ms";
    [ObservableProperty] public partial string TopSuccessRateDisplay { get; set; } = "0.0%";
    [ObservableProperty] public partial string TopThroughputDisplay { get; set; } = "0 tok/s";
    [ObservableProperty] public partial string BaseUrl { get; set; } = "";
    [ObservableProperty] public partial string ApiKey { get; set; } = "";
    [ObservableProperty] public partial string Model { get; set; } = "";
    [ObservableProperty] public partial BatchRunMode SelectedRunMode { get; set; } = BatchRunMode.Quick;

    public bool IsQuickRunMode => SelectedRunMode == BatchRunMode.Quick;
    public bool IsDeepRunMode => SelectedRunMode == BatchRunMode.Deep;
    public bool CanChangeRunMode => !IsRunning;
    public string SelectedRunModeText => SelectedRunMode == BatchRunMode.Deep ? "深度测试" : "快速测试";
    public string StartBatchButtonText => SelectedRunMode == BatchRunMode.Deep ? "开始深度" : "开始快速";
    public string QueueTitle => SelectedRunMode == BatchRunMode.Deep ? "深度测试队列" : "快速测试队列";
    public string QueuePlanText => SelectedRunMode == BatchRunMode.Deep
        ? $"基准 {BaselineScenarioCount} 项 + 独立吞吐 + 能力深测 {DeepSupplementalScenarioCount} 项 + 稳定 {DeepStabilityRounds} 轮"
        : $"基础 {BaselineScenarioCount} 项 + 独立吞吐";
    public string PlannedStageCountText => SelectedRunMode == BatchRunMode.Deep
        ? $"{DeepPlannedStepCount} 个阶段"
        : $"{QuickPlannedStepCount} 个阶段";

    /// <summary>The site editor sub-ViewModel for managing batch site entries.</summary>
    public BatchSiteEditorViewModel SiteEditor { get; } = new();

    public event EventHandler? ProxyBatchEditorOpenRequested;
    public event EventHandler? ProxyBatchEditorCloseRequested;
    public event EventHandler? TransparentProxyRoutesSynced;

    /// <summary>
    /// Summary text showing how many sites are selected for the execution plan.
    /// </summary>
    public string SelectionSummary
    {
        get
        {
            var total = SiteEditor.Sites.Count;
            var included = SiteEditor.Sites.Count(s => s.IsIncluded);
            if (total == 0) return "尚未配置Site";
            if (included == total) return $"已选择全部 {total} 个Site";
            if (included == 0) return "未选择Site";
            return $"已选择 {included}/{total} 个Site";
        }
    }

    public string ProgressDisplay => $"{Progress:F0}%";
    public string CompletedSitesDisplay => $"{CompletedSites} / {TotalSites}";

    // --- Chart properties for comparison charts ---
    [ObservableProperty] public partial ISeries[] LatencyChartSeries { get; set; } = [];
    [ObservableProperty] public partial Axis[] LatencyChartXAxes { get; set; } = [];
    [ObservableProperty] public partial Axis[] LatencyChartYAxes { get; set; } = [];
    [ObservableProperty] public partial bool HasLatencyChart { get; set; }

    [ObservableProperty] public partial ISeries[] ThroughputChartSeries { get; set; } = [];
    [ObservableProperty] public partial Axis[] ThroughputChartXAxes { get; set; } = [];
    [ObservableProperty] public partial Axis[] ThroughputChartYAxes { get; set; } = [];
    [ObservableProperty] public partial bool HasThroughputChart { get; set; }

    // --- 综合Score comparison polyline chart properties ---
    /// <summary>Points for the first polyline in the composite score comparison chart (empty = no data).</summary>
    [ObservableProperty] public partial PointCollection? CompositePolyline1Points { get; set; }

    /// <summary>Points for the second polyline in the composite score comparison chart (empty = no data).</summary>
    [ObservableProperty] public partial PointCollection? CompositePolyline2Points { get; set; }

    /// <summary>Points for the third polyline in the composite score comparison chart (empty = no data).</summary>
    [ObservableProperty] public partial PointCollection? CompositePolyline3Points { get; set; }

    /// <summary>Whether the composite score polyline chart has data to display.</summary>
    public bool HasCompositeChart => (CompositePolyline1Points?.Count ?? 0) > 0
                                  || (CompositePolyline2Points?.Count ?? 0) > 0
                                  || (CompositePolyline3Points?.Count ?? 0) > 0;

    partial void OnCompositePolyline1PointsChanged(PointCollection? value) => OnPropertyChanged(nameof(HasCompositeChart));
    partial void OnCompositePolyline2PointsChanged(PointCollection? value) => OnPropertyChanged(nameof(HasCompositeChart));
    partial void OnCompositePolyline3PointsChanged(PointCollection? value) => OnPropertyChanged(nameof(HasCompositeChart));

    /// <summary>Heatmap cell tones for the route stability heatmap (6 rows x 6 columns = 36 cells).</summary>
    [ObservableProperty] public partial ObservableCollection<BatchHeatmapCellTone>? HeatmapCellTones { get; set; }

    /// <summary>Whether the heatmap has real data (non-default colors).</summary>
    public bool HasHeatmapData { get; private set; }

    /// <summary>Number of currently active concurrent connections during batch evaluation.</summary>
    [ObservableProperty] public partial int ActiveConnections { get; set; }

    /// <summary>Whether rate limiting is currently active during batch evaluation.</summary>
    [ObservableProperty] public partial bool IsRateLimited { get; set; }

    /// <summary>Display text for rate-limit state: "active" or "inactive".</summary>
    public string RateLimitStateText => IsRateLimited ? "活跃" : "未触发";

    /// <summary>
    /// Deep test queue items showing per-site test status and pass/fail counts.
    /// </summary>
    public ObservableCollection<DeepTestQueueItem> DeepTestQueue { get; } = new();

    public ObservableCollection<BatchRunTimelineItem> RunTimeline { get; } = new();

    /// <summary>
    /// Sortable site ranking entries for the ranking table.
    /// </summary>
    public ObservableCollection<SiteRankEntry> SiteRankings { get; } = new();

    /// <summary>Current sort column name.</summary>
    [ObservableProperty] public partial string SortColumn { get; set; } = "CompositeScore";

    /// <summary>Whether the current sort is ascending.</summary>
    [ObservableProperty] public partial bool SortAscending { get; set; }

    /// <summary>Sort direction indicator for the Rank column.</summary>
    public string RankSortIndicator => SortColumn == "Rank" ? (SortAscending ? "\u25B2" : "\u25BC") : "";

    /// <summary>Sort direction indicator for the Site column.</summary>
    public string SiteSortIndicator => SortColumn == "SiteName" ? (SortAscending ? "\u25B2" : "\u25BC") : "";

    /// <summary>Sort direction indicator for the P50 column.</summary>
    public string P50SortIndicator => SortColumn == "LatencyP50" ? (SortAscending ? "\u25B2" : "\u25BC") : "";

    /// <summary>Sort direction indicator for the Throughput column.</summary>
    public string ThroughputSortIndicator => SortColumn == "Throughput" ? (SortAscending ? "\u25B2" : "\u25BC") : "";

    /// <summary>Sort direction indicator for the Success% column.</summary>
    public string SuccessSortIndicator => SortColumn == "SuccessRate" ? (SortAscending ? "\u25B2" : "\u25BC") : "";

    /// <summary>Sort direction indicator for the Score column.</summary>
    public string ScoreSortIndicator => SortColumn == "CompositeScore" ? (SortAscending ? "\u25B2" : "\u25BC") : "";

    partial void OnIsRateLimitedChanged(bool value) => OnPropertyChanged(nameof(RateLimitStateText));
    partial void OnProgressChanged(double value) => OnPropertyChanged(nameof(ProgressDisplay));
    partial void OnCompletedSitesChanged(int value) => OnPropertyChanged(nameof(CompletedSitesDisplay));
    partial void OnTotalSitesChanged(int value) => OnPropertyChanged(nameof(CompletedSitesDisplay));
    partial void OnSelectedRunModeChanged(BatchRunMode value) => NotifyRunModeProperties();
    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(CanChangeRunMode));

    private void NotifyRunModeProperties()
    {
        OnPropertyChanged(nameof(IsQuickRunMode));
        OnPropertyChanged(nameof(IsDeepRunMode));
        OnPropertyChanged(nameof(SelectedRunModeText));
        OnPropertyChanged(nameof(StartBatchButtonText));
        OnPropertyChanged(nameof(QueueTitle));
        OnPropertyChanged(nameof(QueuePlanText));
        OnPropertyChanged(nameof(PlannedStageCountText));
    }

    public BatchComparisonViewModel()
        : this(new RouteRepository())
    {
    }

    public BatchComparisonViewModel(IRouteRepository routeRepository)
        : this(routeRepository, LoadDefaultEndpointHistoryAsync, SharedEndpointStore.Load)
    {
    }

    public BatchComparisonViewModel(
        IRouteRepository routeRepository,
        Func<CancellationToken, Task<IReadOnlyList<EndpointHistoryItem>>> endpointHistoryLoader,
        Func<SharedEndpointState?> sharedEndpointLoader,
        Func<ProxyEndpointSettings, CancellationToken, Task<ProxyModelCatalogResult>>? modelCatalogFetcher = null,
        Func<string, string, string, List<string>, Task>? modelHistoryRecorder = null,
        Func<string, string, string, Task>? sharedEndpointSaver = null,
        Func<ProxyEndpointSettings, CancellationToken, Task<IReadOnlyList<CachedProxyEndpointModelInfo>>>? cachedModelsLoader = null,
        Func<ProxyEndpointSettings, IReadOnlyList<string>, IProgress<ModelProtocolProbeProgress>?, CancellationToken, Task<IReadOnlyList<ModelProtocolInfo>>>? modelProtocolDetector = null,
        Func<ProxyEndpointSettings, IProgress<ProxyDiagnosticsLiveProgress>?, CancellationToken, Task<ProxyDiagnosticsResult>>? diagnosticsRunner = null,
        Func<ProxyEndpointSettings, int, int, IProgress<string>?, IProgress<ProxyDiagnosticsResult>?, CancellationToken, Task<ProxyStabilityResult>>? stabilityRunner = null,
        Func<ProxyEndpointSettings, IReadOnlyList<int>, IProgress<ProxyConcurrencyPressureStageResult>?, CancellationToken, Task<ProxyConcurrencyPressureResult>>? concurrencyRunner = null,
        Func<ProxyEndpointSettings, ProxyDiagnosticsResult, IProgress<ProxyThroughputBenchmarkLiveProgress>?, CancellationToken, Task<ProxyThroughputBenchmarkResult?>>? throughputBenchmarkRunner = null,
        Func<ProxyEndpointSettings, ProxyDiagnosticsResult, IProgress<ProxyDiagnosticsLiveProgress>?, CancellationToken, Task<ProxyDiagnosticsResult>>? supplementalScenarioRunner = null,
        Func<ProxyEndpointSettings, ProxyModelCatalogResult, CancellationToken, Task>? modelCatalogCacheSaver = null)
    {
        _routeRepository = routeRepository;
        _endpointHistoryLoader = endpointHistoryLoader;
        _sharedEndpointLoader = sharedEndpointLoader;
        _sharedEndpointSaver = sharedEndpointSaver ??
            ((baseUrl, apiKey, model) => SharedEndpointStore.SaveAsync(baseUrl, apiKey, model));
        _modelCatalogFetcher = modelCatalogFetcher ?? _diagnosticsService.FetchModelsAsync;
        _modelCatalogCacheSaver = modelCatalogCacheSaver ?? (modelCatalogFetcher is null
            ? _modelCacheService.SaveCatalogAsync
            : ((_, _, _) => Task.CompletedTask));
        _modelHistoryRecorder = modelHistoryRecorder ??
            ((baseUrl, apiKey, model, models) => new EndpointHistoryStore().RecordWithModelsAsync(baseUrl, apiKey, model, models));
        var useRealProtocolServices = modelCatalogFetcher is null &&
                                      cachedModelsLoader is null &&
                                      modelProtocolDetector is null;
        _modelProtocolDetectionEnabled = useRealProtocolServices || modelProtocolDetector is not null;
        _cachedModelsLoader = cachedModelsLoader ?? (useRealProtocolServices
            ? ((settings, ct) => _modelCacheService.ListModelsAsync(settings.BaseUrl, settings.ApiKey, ct))
            : ((_, _) => Task.FromResult((IReadOnlyList<CachedProxyEndpointModelInfo>)[])));
        _modelProtocolDetector = modelProtocolDetector ?? (useRealProtocolServices
            ? RunModelProtocolDetectionCoreAsync
            : ((_, _, _, _) => Task.FromResult((IReadOnlyList<ModelProtocolInfo>)[])));
        _diagnosticsRunner = diagnosticsRunner ??
            ((settings, progress, ct) => _diagnosticsService.RunAsync(settings, progress, ct));
        _stabilityRunner = stabilityRunner ??
            ((settings, rounds, delayMilliseconds, progress, roundProgress, ct) =>
                _diagnosticsService.RunSeriesAsync(settings, rounds, delayMilliseconds, progress, roundProgress, ct));
        _concurrencyRunner = concurrencyRunner ??
            ((settings, stages, progress, ct) =>
                _diagnosticsService.RunConcurrencyPressureAsync(settings, stages, progress, ct));
        var useRealDiagnosticsRunner = diagnosticsRunner is null;
        _throughputBenchmarkRunner = throughputBenchmarkRunner ?? (useRealDiagnosticsRunner
            ? RunThroughputBenchmarkCoreAsync
            : ((_, _, _, _) => Task.FromResult<ProxyThroughputBenchmarkResult?>(null)));
        _supplementalScenarioRunner = supplementalScenarioRunner ?? (useRealDiagnosticsRunner
            ? RunStandardBatchDeepSupplementalScenariosAsync
            : ((_, baseline, _, _) => Task.FromResult(baseline)));
        // Initialize WinUI-specific point collections only when
        // the XAML runtime is available. In unit test environments these COM types cannot be
        // instantiated, so we leave them null (the XAML binding handles null gracefully).
        try
        {
            CompositePolyline1Points = new PointCollection();
            CompositePolyline2Points = new PointCollection();
            CompositePolyline3Points = new PointCollection();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Running outside WinUI runtime (e.g. unit tests) – leave properties null.
        }

        HeatmapCellTones = CreateDefaultHeatmapTones();

        LoadPersistedEndpoint();
        // Refresh selection summary when sites collection or IsIncluded changes
        SiteEditor.Sites.CollectionChanged += (_, e) =>
        {
            RefreshSelectionSummary();
            if (e.NewItems is not null)
            {
                foreach (BatchSiteEntry site in e.NewItems)
                    site.PropertyChanged += OnSitePropertyChanged;
            }
        };
        foreach (var site in SiteEditor.Sites)
            site.PropertyChanged += OnSitePropertyChanged;

        ApplyZeroHistoryState();
        LoadHistoricalBatchState();
    }

}
