using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using Microsoft.UI.Xaml;
using RelayBench.Core.Models;
using RelayBench.Core.Services;
using RelayBench.Core.Support;
using RelayBench.WinUI.Charts;
using RelayBench.WinUI.Services;
using RelayBench.WinUI.Storage;
using SkiaSharp;
using Windows.ApplicationModel.DataTransfer;

namespace RelayBench.WinUI.ViewModels;

/// <summary>
/// Test mode for single station diagnostics.
/// </summary>
public enum TestMode
{
    Quick,
    Stability,
    Deep,
    Concurrency
}

/// <summary>
/// Represents a single scenario result for the Deep mode pass/fail grid.
/// </summary>
public sealed class DeepScenarioResult : ObservableObject
{
    public int Order { get; init; }
    public string SectionName { get; init; } = "";
    public string SectionHint { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public bool? Passed { get; set; }
    public string StatusText { get; set; } = "0";
    public string Latency { get; set; } = "0 ms";
    public int? StatusCode { get; init; }
    public double? MetricValueMs { get; init; }
    public string MetricText { get; init; } = "0 ms";
    public bool ReceivedDone { get; init; }
    public string DetailText { get; init; } = "";
    public string PreviewText { get; init; } = "";
    public ProxyProbeTrace? Trace { get; init; }
    public string StatusGlyph => Passed == true ? "\uE73E" : Passed == false ? "\uE711" : "\uE9CE";
    public Visibility PassedVisibility => Passed == true ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FailedVisibility => Passed == false ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PendingVisibility => Passed is null ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>
/// Metadata row for the stability trend surface. This preserves the useful
/// WPF trend-row summaries without coupling WinUI to the old bitmap renderer.
/// </summary>
public sealed class StabilityTrendRow : ObservableObject
{
    public string Title { get; init; } = "";
    public string ValueText { get; init; } = "";
    public string StatusText { get; init; } = "";
    public string HintText { get; init; } = "";
    public string ValueSummaryText { get; init; } = "";
    public string DetailText { get; init; } = "";
    public bool IsRunning { get; init; }
    public string Tone { get; init; } = "Neutral";
}

/// <summary>
/// Represents a single concurrency level result.
/// </summary>
public sealed class ConcurrencyLevelResult : ObservableObject
{
    public int Level { get; init; }
    public int TotalRequests { get; init; }
    public int SuccessCount { get; init; }
    public int RateLimitedCount { get; init; }
    public int ServerErrorCount { get; init; }
    public int TimeoutCount { get; init; }
    public double Throughput { get; set; }
    public double ErrorRate { get; set; }
    public double SuccessRate { get; set; }
    public double? P50ChatLatencyMs { get; init; }
    public double? P95TtftMs { get; init; }
    public double? AverageTokensPerSecond { get; init; }
    public double SuccessRateRatio => Math.Clamp(SuccessRate, 0d, 100d);
    public double ErrorRateRatio => Math.Clamp(ErrorRate, 0d, 100d);
    public double ThroughputRatio { get; set; }
    public double P50LatencyRatio { get; set; }
    public double P95TtftRatio { get; set; }
    public bool IsStableLimit { get; init; }
    public bool IsRateLimitStart { get; init; }
    public bool IsHighRisk { get; init; }
    public string VerdictText { get; init; } = "0";
    public string SummaryText { get; init; } = "";
    public string DetailText { get; init; } = "";
    public string SuccessRateText { get; set; } = "0.0%";
    public string ThroughputText { get; set; } = "0 tok/s";
    public string ErrorRateText { get; set; } = "0%";
    public string P50LatencyText { get; set; } = "0 ms";
    public string P95TtftText { get; set; } = "0 ms";
    public string StatusText { get; set; } = "0";
    public string RequestMixText { get; set; } = "0/0";
    public string LevelText => $"x{Level}";
    public string CompletedText { get; set; } = "0";
    public string ErrorBreakdownText { get; set; } = "0";
    public Visibility StableStatusVisibility => IsStableStatus ? Visibility.Visible : Visibility.Collapsed;
    public Visibility WarningStatusVisibility => IsWarningStatus ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DangerStatusVisibility => IsDangerStatus ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NeutralStatusVisibility => IsStableStatus || IsWarningStatus || IsDangerStatus ? Visibility.Collapsed : Visibility.Visible;

    private bool IsStableStatus => StatusText is "稳定" or "稳定上限";
    private bool IsWarningStatus => StatusText is "观察" or "限流";
    private bool IsDangerStatus => StatusText == "高风险";
}

public sealed class MultiModelSpeedResult : ObservableObject
{
    public string Model { get; init; } = "";
    public bool Success { get; init; }
    public string ThroughputText { get; init; } = "0 tok/s";
    public string StatusText => Success ? "通过" : "失败";
    public string Summary { get; init; } = "";
}

public sealed class ModelProtocolCacheRow
{
    public string Model { get; init; } = "";
    public string PreferredProtocol { get; init; } = "0";
    public string ChatSupport { get; init; } = "0";
    public string ResponsesSupport { get; init; } = "0";
    public string AnthropicSupport { get; init; } = "0";
    public string CheckedAt { get; init; } = "0";
    public string SupportSummary => $"Chat {ChatSupport} / Responses {ResponsesSupport} / Anthropic {AnthropicSupport}";
}

public sealed partial class SingleStationViewModel : ObservableObject
{
    private readonly ProxyDiagnosticsService _diagnosticsService = new();
    private readonly IHistoryRepository _historyRepository = new HistoryRepository();
    private readonly EndpointHistoryStore _historyStore = new();
    private readonly ProxyEndpointModelCacheService _modelCacheService = new(
        Path.Combine(StoragePaths.Root, "endpoint-model-cache.sqlite"));
    private CancellationTokenSource? _cts;
    private readonly List<double> _throughputChartSamples = [];
    private readonly List<double> _streamingInputRateSamples = [];
    private readonly List<double> _streamingOutputRateSamples = [];
    private DateTimeOffset _lastThroughputChartRefresh = DateTimeOffset.MinValue;
    private DateTimeOffset _lastThroughputLogAt = DateTimeOffset.MinValue;
    private int _lastThroughputCompletedSampleCount = -1;
    private const int ThroughputChartSampleLimit = 36;
    private const int ModelProtocolProbeMaxConcurrency = 8;
    private const double MaximumDisplayedTokensPerSecond = 10_000d;
    private static readonly TimeSpan ThroughputChartRefreshInterval = TimeSpan.FromMilliseconds(1200);
    private static readonly TimeSpan ThroughputLogInterval = TimeSpan.FromSeconds(3);

    public event EventHandler? ProxyEndpointHistoryOpenRequested;
    public event EventHandler? ProxyMultiModelPickerOpenRequested;

    // --- Input fields ---
    [ObservableProperty] public partial int ProtocolIndex { get; set; } = 1; // 0=HTTPS, 1=HTTP (default HTTP)
    [ObservableProperty] public partial string BaseUrl { get; set; } = "";
    [ObservableProperty] public partial string ApiKey { get; set; } = "";
    [ObservableProperty] public partial string Model { get; set; } = "";
    [ObservableProperty] public partial bool IgnoreTlsErrors { get; set; }
    [ObservableProperty] public partial int TimeoutSeconds { get; set; } = 30;

    // --- Advanced execution options migrated from the WPF single-station workflow ---
    [ObservableProperty] public partial int StabilityRounds { get; set; } = 5;
    [ObservableProperty] public partial int StabilityDelayMs { get; set; } = 1200;
    [ObservableProperty] public partial bool EnableSemanticStabilitySampling { get; set; }
    [ObservableProperty] public partial bool EnableLongStreamingTest { get; set; }
    [ObservableProperty] public partial int LongStreamSegments { get; set; } = 72;
    [ObservableProperty] public partial bool EnableProtocolCompatibilityTest { get; set; } = true;
    [ObservableProperty] public partial bool EnableErrorTransparencyTest { get; set; } = true;
    [ObservableProperty] public partial bool EnableStreamingIntegrityTest { get; set; } = true;
    [ObservableProperty] public partial bool EnableMultiModalTest { get; set; } = true;
    [ObservableProperty] public partial bool EnableCacheMechanismTest { get; set; } = true;
    [ObservableProperty] public partial bool EnableInstructionFollowingTest { get; set; } = true;
    [ObservableProperty] public partial bool EnableDataExtractionTest { get; set; } = true;
    [ObservableProperty] public partial bool EnableStructuredOutputEdgeTest { get; set; } = true;
    [ObservableProperty] public partial bool EnableToolCallDeepTest { get; set; } = true;
    [ObservableProperty] public partial bool EnableReasonMathConsistencyTest { get; set; }
    [ObservableProperty] public partial bool EnableCodeBlockDisciplineTest { get; set; } = true;
    [ObservableProperty] public partial bool EnableCacheIsolationTest { get; set; }
    [ObservableProperty] public partial string CacheIsolationAlternateApiKey { get; set; } = "";
    [ObservableProperty] public partial bool EnableOfficialReferenceIntegrityTest { get; set; }
    [ObservableProperty] public partial string OfficialReferenceBaseUrl { get; set; } = "";
    [ObservableProperty] public partial string OfficialReferenceApiKey { get; set; } = "";
    [ObservableProperty] public partial string OfficialReferenceModel { get; set; } = "";
    [ObservableProperty] public partial string CapabilityEmbeddingsModel { get; set; } = "";
    [ObservableProperty] public partial string CapabilityImagesModel { get; set; } = "";
    [ObservableProperty] public partial string CapabilityAudioTranscriptionModel { get; set; } = "";
    [ObservableProperty] public partial string CapabilityAudioSpeechModel { get; set; } = "";
    [ObservableProperty] public partial string CapabilityModerationModel { get; set; } = "";
    [ObservableProperty] public partial string MultiModelBenchmarkModelsText { get; set; } = "";

    public string AdvancedExecutionSummary
        => $"深度探针 {CountEnabledDeepProbes()} 项已启用 / 非聊天模型 {CountConfiguredCapabilityModels()}/5 / 多模型测速 {GetSelectedMultiModelBenchmarkModels().Length} 个";

    public string MultiModelBenchmarkModelsDisplay
    {
        get
        {
            var models = GetSelectedMultiModelBenchmarkModels();
            return models.Length switch
            {
                0 => "选择多模型",
                <= 2 => string.Join("、", models),
                _ => $"{models.Length} 个模型：{string.Join("、", models.Take(2))} 等"
            };
        }
    }

    public string ConcurrencyPlanSummary
        => "并发档位 1 / 2 / 4 / 8 / 16；每档发送并发数 x 2 个请求，评估成功率、429、超时、P95 TTFT 与 tok/s。";

    /// <summary>Protocol prefix derived from ProtocolIndex.</summary>
    public string ProtocolPrefix => ProtocolIndex == 0 ? "https://" : "http://";

    /// <summary>Available models fetched from the endpoint for the model picker.</summary>
    public ObservableCollection<string> AvailableModels { get; } = new();

    /// <summary>Models fetched from the official reference endpoint.</summary>
    public ObservableCollection<string> OfficialReferenceModels { get; } = new();

    // --- Common state ---
    [ObservableProperty] public partial bool IsTesting { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; } = "Ready";
    [ObservableProperty] public partial TestMode SelectedTestMode { get; set; } = TestMode.Quick;

    // --- New properties for redesigned UI ---
    [ObservableProperty] public partial string RawResponseJson { get; set; } = "";
    [ObservableProperty] public partial string ResponseHeaders { get; set; } = "";
    [ObservableProperty] public partial string TraceTimings { get; set; } = "";
    [ObservableProperty] public partial string TestLog { get; set; } = "";
    [ObservableProperty] public partial string EntryNodeName { get; set; } = "0";
    [ObservableProperty] public partial string ProtocolVersion { get; set; } = "0";
    [ObservableProperty] public partial string ProductName { get; set; } = "RelayBench";
    [ObservableProperty] public partial string ResponseContentType { get; set; } = "application/json";
    [ObservableProperty] public partial string TestTimestamp { get; set; } = "0";
    [ObservableProperty] public partial bool IsRawResponseExpanded { get; set; }
    [ObservableProperty] public partial string RawResponseLineNumbers { get; set; } = "1";
    [ObservableProperty] public partial string CompletionReason { get; set; } = "stop";
    [ObservableProperty] public partial string SuccessRateDetail { get; set; } = "0/0";
    [ObservableProperty] public partial string LatencyThreshold { get; set; } = "P95 < 2,500 ms";
    [ObservableProperty] public partial string ErrorRateDisplay { get; set; } = "0%";
    [ObservableProperty] public partial string CapabilitySummary { get; set; } = "\u5168\u90E8\u901A\u8FC7";

    public string StatusBadgeText => ChatStatusCode.Contains("200", StringComparison.OrdinalIgnoreCase)
        ? "200 OK"
        : ChatStatusCode;

    public Visibility StatusBadgeSuccessVisibility => IsSuccessfulStatusCode ? Visibility.Visible : Visibility.Collapsed;

    public Visibility StatusBadgeErrorVisibility => IsSuccessfulStatusCode ? Visibility.Collapsed : Visibility.Visible;

    public string VerdictDisplay => Verdict is "Pass" ? "\u901A\u8FC7" : Verdict is "Fail" ? "\u5931\u8D25" : Verdict;

    public Visibility VerdictPassVisibility => IsPassVerdict ? Visibility.Visible : Visibility.Collapsed;

    public Visibility VerdictFailVisibility => IsFailVerdict ? Visibility.Visible : Visibility.Collapsed;

    public Visibility VerdictNeutralVisibility => IsPassVerdict || IsFailVerdict ? Visibility.Collapsed : Visibility.Visible;

    public string VerdictGlyph => VerdictDisplay == "\u901A\u8FC7" ? "\uE73E" : VerdictDisplay == "\u5931\u8D25" ? "\uE711" : "\uE9CE";

    public double ResponseViewerMaxHeight => IsRawResponseExpanded ? 560 : 300;

    public string RawResponseToggleGlyph => IsRawResponseExpanded ? "\uE73F" : "\uE740";

    public string RawResponseToggleTooltip => IsRawResponseExpanded ? "\u6536\u8D77" : "\u5C55\u5F00";

    /// <summary>Computed display string for entry node in the status banner.</summary>
    public string EntryNodeDisplay => string.IsNullOrEmpty(EntryNodeName) || EntryNodeName == "0"
        ? "" : $"\u5165\u53E3: {EntryNodeName}";

    /// <summary>Status banner glyph (checkmark or X).</summary>
    public string StatusBannerGlyph => Verdict == "Pass" || Verdict == "\u901A\u8FC7"
        ? "\uE73E" : Verdict == "Fail" || Verdict == "\u5931\u8D25" ? "\uE711" : "\uE9CE";

    private bool IsSuccessfulStatusCode => ChatStatusCode.Contains("200", StringComparison.OrdinalIgnoreCase);

    private bool IsPassVerdict => VerdictDisplay == "\u901A\u8FC7";

    private bool IsFailVerdict => VerdictDisplay == "\u5931\u8D25";

    /// <summary>Index-based binding for test mode ComboBox.</summary>
    public int SelectedTestModeIndex
    {
        get => (int)SelectedTestMode;
        set => SelectedTestMode = (TestMode)value;
    }

    // --- Streaming token chart ---
    [ObservableProperty] public partial ISeries[] StreamingTokenChartSeries { get; set; } = [];
    [ObservableProperty] public partial Axis[] StreamingTokenChartYAxes { get; set; } = [];
    [ObservableProperty] public partial Axis[] StreamingTokenChartXAxes { get; set; } = [];

    // --- Mode selection helpers for RadioButton binding ---
    public bool IsQuickMode
    {
        get => SelectedTestMode == TestMode.Quick;
        set { if (value) SelectedTestMode = TestMode.Quick; }
    }
    public bool IsStabilityMode
    {
        get => SelectedTestMode == TestMode.Stability;
        set { if (value) SelectedTestMode = TestMode.Stability; }
    }
    public bool IsDeepMode
    {
        get => SelectedTestMode == TestMode.Deep;
        set { if (value) SelectedTestMode = TestMode.Deep; }
    }
    public bool IsConcurrencyMode
    {
        get => SelectedTestMode == TestMode.Concurrency;
        set { if (value) SelectedTestMode = TestMode.Concurrency; }
    }

    // --- Visibility helpers for mode panels ---
    public bool IsQuickModeVisible => SelectedTestMode == TestMode.Quick;
    public bool IsStabilityModeVisible => SelectedTestMode == TestMode.Stability;
    public bool IsDeepModeVisible => SelectedTestMode == TestMode.Deep;
    public bool IsConcurrencyModeVisible => SelectedTestMode == TestMode.Concurrency;
    public bool IsTestOptionsVisible => SelectedTestMode != TestMode.Quick;
    public bool IsSideTestOptionsVisible => SelectedTestMode != TestMode.Deep;

    // --- Quick mode results ---
    [ObservableProperty] public partial string TotalTime { get; set; } = "0s";
    [ObservableProperty] public partial string ModelsLatency { get; set; } = "0 ms";
    [ObservableProperty] public partial string ChatLatency { get; set; } = "0 ms";
    [ObservableProperty] public partial string StreamTtft { get; set; } = "0 ms";
    [ObservableProperty] public partial string ChatStatusCode { get; set; } = "0";
    [ObservableProperty] public partial string ModelCount { get; set; } = "0";
    [ObservableProperty] public partial string ChatPreview { get; set; } = "";
    [ObservableProperty] public partial string ProbeResultSummary { get; set; } = "";
    [ObservableProperty] public partial string ProtocolPreferred { get; set; } = "0";
    [ObservableProperty] public partial string Verdict { get; set; } = "0";
    [ObservableProperty] public partial string VerdictReason { get; set; } = "";
    [ObservableProperty] public partial bool ChatSupported { get; set; }
    [ObservableProperty] public partial bool ResponsesSupported { get; set; }
    [ObservableProperty] public partial bool AnthropicSupported { get; set; }
    [ObservableProperty] public partial ProtocolDetectionResult ProtocolDetection { get; set; } = ProtocolDetectionResult.Unknown;
    [ObservableProperty] public partial bool HasQuickResults { get; set; }

    // --- Quick mode stats row ---
    [ObservableProperty] public partial string QuickRequestSize { get; set; } = "0 tokens";
    [ObservableProperty] public partial string QuickResponseSize { get; set; } = "0 tokens";
    [ObservableProperty] public partial string QuickSuccessRate { get; set; } = "0.0%";
    [ObservableProperty] public partial string QuickAvgLatency { get; set; } = "0 ms";
    [ObservableProperty] public partial string QuickThroughput { get; set; } = "0 tok/s";

    // --- Quick mode latency tooltip ---
    [ObservableProperty] public partial string LatencyTooltipTime { get; set; } = "History样本 0";
    [ObservableProperty] public partial string LatencyTooltipP50 { get; set; } = "最小: 0 ms";
    [ObservableProperty] public partial string LatencyTooltipP95 { get; set; } = "平均: 0 ms";
    [ObservableProperty] public partial string LatencyTooltipP99 { get; set; } = "最大: 0 ms";

    // --- Streaming token legend labels ---
    [ObservableProperty] public partial string StreamingInputLabel { get; set; } = "端到端均值 (0 tok/s)";
    [ObservableProperty] public partial string StreamingOutputLabel { get; set; } = "生成均值 (0 tok/s)";

    // --- Quick mode charts ---
    [ObservableProperty] public partial ISeries[] QuickLatencyChartSeries { get; set; } = [];
    [ObservableProperty] public partial Axis[] QuickLatencyChartYAxes { get; set; } = [];
    [ObservableProperty] public partial Axis[] QuickLatencyChartXAxes { get; set; } = [];
    [ObservableProperty] public partial ISeries[] QuickTtftChartSeries { get; set; } = [];
    [ObservableProperty] public partial Axis[] QuickTtftChartYAxes { get; set; } = [];
    [ObservableProperty] public partial Axis[] QuickTtftChartXAxes { get; set; } = [];
    [ObservableProperty] public partial ISeries[] QuickThroughputChartSeries { get; set; } = [];
    [ObservableProperty] public partial Axis[] QuickThroughputChartYAxes { get; set; } = [];
    [ObservableProperty] public partial Axis[] QuickThroughputChartXAxes { get; set; } = [];

    // --- Stability mode results ---
    [ObservableProperty] public partial string StabilityP50 { get; set; } = "0 ms";
    [ObservableProperty] public partial string StabilityP95 { get; set; } = "0 ms";
    [ObservableProperty] public partial string StabilityP99 { get; set; } = "0 ms";
    [ObservableProperty] public partial string StabilityHealthScore { get; set; } = "0/100";
    [ObservableProperty] public partial string StabilitySuccessRate { get; set; } = "0.0%";
    [ObservableProperty] public partial string StabilitySummary { get; set; } = "";
    [ObservableProperty] public partial int StabilityCompletedRounds { get; set; }
    [ObservableProperty] public partial int StabilityTotalRounds { get; set; } = 10;
    [ObservableProperty] public partial ISeries[] StabilityChartSeries { get; set; } = [];
    [ObservableProperty] public partial Axis[] StabilityChartYAxes { get; set; } = [];
    [ObservableProperty] public partial Axis[] StabilityChartXAxes { get; set; } = [];
    [ObservableProperty] public partial bool HasStabilityResults { get; set; }
    public ObservableCollection<StabilityTrendRow> StabilityTrendRows { get; } = new();

    // --- Deep mode results ---
    [ObservableProperty] public partial bool HasDeepResults { get; set; }
    [ObservableProperty] public partial string DeepPassCount { get; set; } = "0";
    [ObservableProperty] public partial string DeepFailCount { get; set; } = "0";
    [ObservableProperty] public partial string DeepSummary { get; set; } = "";

    // --- Concurrency mode results ---
    [ObservableProperty] public partial bool HasConcurrencyResults { get; set; }
    [ObservableProperty] public partial string ConcurrencyPeakThroughput { get; set; } = "0 tok/s";
    [ObservableProperty] public partial string ConcurrencyPeakLevel { get; set; } = "0";
    [ObservableProperty] public partial string ConcurrencyMaxErrorRate { get; set; } = "0%";
    [ObservableProperty] public partial string ConcurrencyStableLimit { get; set; } = "0";
    [ObservableProperty] public partial string ConcurrencyPracticalLimit { get; set; } = "0";
    [ObservableProperty] public partial string ConcurrencyRateLimitStart { get; set; } = "0";
    [ObservableProperty] public partial string ConcurrencyHighRiskLevel { get; set; } = "0";
    [ObservableProperty] public partial string ConcurrencySummary { get; set; } = "";
    [ObservableProperty] public partial ISeries[] ConcurrencyChartSeries { get; set; } = [];
    [ObservableProperty] public partial Axis[] ConcurrencyChartYAxes { get; set; } = [];
    [ObservableProperty] public partial Axis[] ConcurrencyChartXAxes { get; set; } = [];

    // --- Mode-specific KPI ---
    [ObservableProperty] public partial string Kpi1Label { get; set; } = "";
    [ObservableProperty] public partial string Kpi1Value { get; set; } = "0";
    [ObservableProperty] public partial string Kpi2Label { get; set; } = "";
    [ObservableProperty] public partial string Kpi2Value { get; set; } = "0";
    [ObservableProperty] public partial string Kpi3Label { get; set; } = "";
    [ObservableProperty] public partial string Kpi3Value { get; set; } = "0";
    [ObservableProperty] public partial string Kpi4Label { get; set; } = "";
    [ObservableProperty] public partial string Kpi4Value { get; set; } = "0";

    public ObservableCollection<CapabilityCellState> Capabilities { get; } = new()
    {
        new("/models", "\uE8B7", CapabilityState.Unknown, "\u6A21\u578B\u5217\u8868", "\u8017\u65F6", "0 ms", "\u6A21\u578B\u6570", "0"),
        new("聊天补全", "\uE8F2", CapabilityState.Unknown, "\u804A\u5929\u8865\u5168", "TTFT", "0 ms", "\u6D41\u5F0F\u8F93\u51FA", "0"),
        new("响应接口", "\uE8A5", CapabilityState.Unknown, "\u54CD\u5E94\u63A5\u53E3", "TTFT", "0 ms", "\u6279\u91CF\u652F\u6301", "0"),
        new("消息接口", "\uE8D4", CapabilityState.Unknown, "\u6D88\u606F\u63A5\u53E3", "TTFT", "0 ms", "\u591A\u8F6E\u652F\u6301", "0"),
        new("\u7ED3\u6784\u5316\u8F93\u51FA", "\uE943", CapabilityState.Unknown, "JSON Schema", "\u4E25\u683C\u6A21\u5F0F", "0", "\u6570\u636E\u8F93\u51FA", "0"),
        new("工具调用", "\uE90F", CapabilityState.Unknown, "\u5DE5\u5177\u8C03\u7528", "\u51FD\u6570\u8C03\u7528", "0", "\u5E76\u884C\u8C03\u7528", "0"),
        new("\u9519\u8BEF\u900F\u4F20", "\uE7BA", CapabilityState.Unknown, "\u9519\u8BEF\u5904\u7406", "4xx \u900F\u4F20", "0", "5xx \u900F\u4F20", "0"),
        new("\u591A\u6A21\u6001", "\uEB9F", CapabilityState.Unknown, "\u56FE\u6587\u8F93\u5165", "\u56FE\u50CF\u8F93\u5165", "0", "\u56FE\u7247\u8F93\u5165", "0"),
        // Phase 19: Non-chat API capability matrix entries (indices 8-11)
        new("向量嵌入", "\uE8C1", CapabilityState.Unknown, "\u5411\u91CF\u5D4C\u5165", "\u5EF6\u8FDF", "0 ms", "\u7EF4\u5EA6", "0"),
        new("图像生成", "\uEB9F", CapabilityState.Unknown, "\u56FE\u50CF\u751F\u6210", "\u5EF6\u8FDF", "0 ms", "\u683C\u5F0F", "0"),
        new("语音转写", "\uE8D6", CapabilityState.Unknown, "\u97F3\u9891\u8F6C\u5199", "\u5EF6\u8FDF", "0 ms", "\u652F\u6301", "0"),
        new("语音合成", "\uE720", CapabilityState.Unknown, "\u97F3\u9891\u5408\u6210", "\u5EF6\u8FDF", "0 ms", "\u652F\u6301", "0"),
        new("内容审核", "\uE8E8", CapabilityState.Unknown, "\u5185\u5BB9\u5BA1\u6838", "\u5EF6\u8FDF", "0 ms", "\u7C7B\u522B", "0"),
    };

    // --- 接口History ---
    public ObservableCollection<EndpointHistoryItem> EndpointHistory { get; } = new();

    public ObservableCollection<DeepScenarioResult> DeepScenarios { get; } = new();
    public ObservableCollection<ConcurrencyLevelResult> ConcurrencyLevels { get; } = new();
    public ObservableCollection<MultiModelSpeedResult> MultiModelSpeedResults { get; } = new();
    public ObservableCollection<ModelProtocolCacheRow> ModelProtocolCacheRows { get; } = new();

    [ObservableProperty] public partial string ModelProtocolCacheSummary { get; set; } = "尚未加载协议记录";
    [ObservableProperty] public partial bool HasModelProtocolCacheRows { get; set; }
    [ObservableProperty] public partial bool IsProxyCapabilityConfigOpen { get; set; } = true;
    [ObservableProperty] public partial bool IsProxyChartExpanded { get; set; }
    [ObservableProperty] public partial bool IsProxyChartImageOnlyMode { get; set; }

    public SingleStationViewModel()
    {
        LoadPersistedEndpoint();
        ApplyZeroHistoryState();
        LoadHistoricalSingleStationState();
        UpdateKpiLabels(SelectedTestMode);
        _ = RefreshModelProtocolCacheAsync();
    }

}
