using System.Collections.ObjectModel;
using System.Linq;
using NetTest.App.Infrastructure;
using NetTest.Core.Models;

namespace NetTest.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private bool _suppressProxyDiagnosticPresetSync;
    private string _selectedProxyDiagnosticPresetKey = "deep";
    private bool _proxyEnableLongStreamingTest;
    private bool _proxyEnableProtocolCompatibilityTest;
    private bool _proxyEnableErrorTransparencyTest;
    private bool _proxyEnableStreamingIntegrityTest;
    private bool _proxyEnableMultiModalTest;
    private bool _proxyEnableCacheMechanismTest;
    private bool _proxyEnableCacheIsolationTest;
    private string _proxyCacheIsolationAlternateApiKey = string.Empty;
    private bool _proxyEnableOfficialReferenceIntegrityTest;
    private string _proxyOfficialReferenceBaseUrl = string.Empty;
    private string _proxyOfficialReferenceApiKey = string.Empty;
    private string _proxyOfficialReferenceModel = string.Empty;
    private bool _proxyBatchEnableLongStreamingTest;
    private string _proxyLongStreamSegmentsText = "72";
    private string _proxyLongStreamingSummary = "长流稳定简测尚未运行。";
    private string _proxyTraceabilitySummary = "可追溯信息会在诊断后显示。";
    private string _proxyOverviewVerdict = "等待诊断";
    private string _proxyOverviewLatency = "普通 -- / TTFT --";
private string _proxyOverviewThroughput = "独立吞吐 --";
    private string _proxyOverviewLongStream = "未启用";
    private string _proxyOverviewTraceability = "等待诊断";
    private string _proxyOverviewBatch = "暂无推荐";

    public ObservableCollection<SelectionOption> ProxyDiagnosticPresetOptions { get; } =
    [
        new("deep", "标准深测"),
        new("custom", "自定义")
    ];

    public string SelectedProxyDiagnosticPresetKey
    {
        get => _selectedProxyDiagnosticPresetKey;
        set
        {
            var resolved = ResolveProxyDiagnosticPresetKey(value);
            if (SetProperty(ref _selectedProxyDiagnosticPresetKey, resolved))
            {
                ApplyProxyDiagnosticPreset(resolved);
                OnPropertyChanged(nameof(ProxyDiagnosticsPresetDescription));
                OnPropertyChanged(nameof(ProxyDiagnosticsExecutionSummary));
            }
        }
    }

    public string ProxyDiagnosticsPresetDescription
        => SelectedProxyDiagnosticPresetKey switch
        {
            "custom" => "自定义：按需勾选专项能力，只补测你关心的深度项。",
            _ => "标准深测：默认开启协议兼容、错误透传、流式完整性、官方对照、多模态、缓存机制和缓存隔离。"
        };

    public bool ProxyEnableLongStreamingTest
    {
        get => _proxyEnableLongStreamingTest;
        set
        {
            if (SetProperty(ref _proxyEnableLongStreamingTest, value))
            {
                RefreshProxyOverviewSummary();
            }
        }
    }

    public bool ProxyEnableProtocolCompatibilityTest
    {
        get => _proxyEnableProtocolCompatibilityTest;
        set
        {
            if (SetProperty(ref _proxyEnableProtocolCompatibilityTest, value))
            {
                SyncProxyDiagnosticPresetFromFlags();
                OnPropertyChanged(nameof(ProxyDiagnosticsExecutionSummary));
            }
        }
    }

    public bool ProxyEnableErrorTransparencyTest
    {
        get => _proxyEnableErrorTransparencyTest;
        set
        {
            if (SetProperty(ref _proxyEnableErrorTransparencyTest, value))
            {
                SyncProxyDiagnosticPresetFromFlags();
                OnPropertyChanged(nameof(ProxyDiagnosticsExecutionSummary));
            }
        }
    }

    public bool ProxyEnableStreamingIntegrityTest
    {
        get => _proxyEnableStreamingIntegrityTest;
        set
        {
            if (SetProperty(ref _proxyEnableStreamingIntegrityTest, value))
            {
                SyncProxyDiagnosticPresetFromFlags();
                OnPropertyChanged(nameof(ProxyDiagnosticsExecutionSummary));
            }
        }
    }

    public bool ProxyEnableMultiModalTest
    {
        get => _proxyEnableMultiModalTest;
        set
        {
            if (SetProperty(ref _proxyEnableMultiModalTest, value))
            {
                SyncProxyDiagnosticPresetFromFlags();
                OnPropertyChanged(nameof(ProxyDiagnosticsExecutionSummary));
            }
        }
    }

    public bool ProxyEnableCacheMechanismTest
    {
        get => _proxyEnableCacheMechanismTest;
        set
        {
            if (SetProperty(ref _proxyEnableCacheMechanismTest, value))
            {
                SyncProxyDiagnosticPresetFromFlags();
                OnPropertyChanged(nameof(ProxyDiagnosticsExecutionSummary));
            }
        }
    }

    public bool ProxyEnableCacheIsolationTest
    {
        get => _proxyEnableCacheIsolationTest;
        set
        {
            if (SetProperty(ref _proxyEnableCacheIsolationTest, value))
            {
                SyncProxyDiagnosticPresetFromFlags();
                OnPropertyChanged(nameof(ProxyDiagnosticsExecutionSummary));
            }
        }
    }

    public string ProxyCacheIsolationAlternateApiKey
    {
        get => _proxyCacheIsolationAlternateApiKey;
        set
        {
            if (SetProperty(ref _proxyCacheIsolationAlternateApiKey, value))
            {
                OnPropertyChanged(nameof(ProxyDiagnosticsExecutionSummary));
            }
        }
    }

    public bool ProxyEnableOfficialReferenceIntegrityTest
    {
        get => _proxyEnableOfficialReferenceIntegrityTest;
        set
        {
            if (SetProperty(ref _proxyEnableOfficialReferenceIntegrityTest, value))
            {
                SyncProxyDiagnosticPresetFromFlags();
                OnPropertyChanged(nameof(ProxyDiagnosticsExecutionSummary));
            }
        }
    }

    public string ProxyOfficialReferenceBaseUrl
    {
        get => _proxyOfficialReferenceBaseUrl;
        set
        {
            if (SetProperty(ref _proxyOfficialReferenceBaseUrl, value))
            {
                OnPropertyChanged(nameof(ProxyDiagnosticsExecutionSummary));
            }
        }
    }

    public string ProxyOfficialReferenceApiKey
    {
        get => _proxyOfficialReferenceApiKey;
        set
        {
            if (SetProperty(ref _proxyOfficialReferenceApiKey, value))
            {
                OnPropertyChanged(nameof(ProxyDiagnosticsExecutionSummary));
            }
        }
    }

    public string ProxyOfficialReferenceModel
    {
        get => _proxyOfficialReferenceModel;
        set
        {
            if (SetProperty(ref _proxyOfficialReferenceModel, value))
            {
                OnPropertyChanged(nameof(ProxyDiagnosticsExecutionSummary));
            }
        }
    }

    public bool ProxyBatchEnableLongStreamingTest
    {
        get => _proxyBatchEnableLongStreamingTest;
        set
        {
            if (SetProperty(ref _proxyBatchEnableLongStreamingTest, value))
            {
                OnPropertyChanged(nameof(ProxyBatchExecutionPlanSummary));
                RefreshProxyOverviewSummary();
            }
        }
    }

    public string ProxyLongStreamSegmentsText
    {
        get => _proxyLongStreamSegmentsText;
        set
        {
            if (SetProperty(ref _proxyLongStreamSegmentsText, value))
            {
                OnPropertyChanged(nameof(ProxyDiagnosticsExecutionSummary));
                OnPropertyChanged(nameof(ProxyBatchExecutionPlanSummary));
            }
        }
    }

    public string ProxyDiagnosticsExecutionSummary
    {
        get
        {
            return
                $"当前方案：{GetProxyDiagnosticPresetDisplayName(SelectedProxyDiagnosticPresetKey)}；" +
                "深度测试只负责专项能力探针，快速测试与长流稳定简测独立控制；" +
                $"协议兼容：{(ProxyEnableProtocolCompatibilityTest ? "开启" : "关闭")}；" +
                $"错误透传：{(ProxyEnableErrorTransparencyTest ? "开启" : "关闭")}；" +
                $"流式完整性：{(ProxyEnableStreamingIntegrityTest ? "开启" : "关闭")}；" +
                $"官方对照：{DescribeOfficialReferenceExecutionState()}；" +
                $"多模态：{(ProxyEnableMultiModalTest ? "开启" : "关闭")}；" +
                $"缓存机制：{(ProxyEnableCacheMechanismTest ? "开启" : "关闭")}；" +
                $"缓存隔离：{DescribeCacheIsolationExecutionState()}。";
        }
    }

    public string ProxyLongStreamingSummary
    {
        get => _proxyLongStreamingSummary;
        private set => SetProperty(ref _proxyLongStreamingSummary, value);
    }

    public string ProxyTraceabilitySummary
    {
        get => _proxyTraceabilitySummary;
        private set => SetProperty(ref _proxyTraceabilitySummary, value);
    }

    public string ProxyOverviewVerdict
    {
        get => _proxyOverviewVerdict;
        private set => SetProperty(ref _proxyOverviewVerdict, value);
    }

    public string ProxyOverviewLatency
    {
        get => _proxyOverviewLatency;
        private set => SetProperty(ref _proxyOverviewLatency, value);
    }

    public string ProxyOverviewThroughput
    {
        get => _proxyOverviewThroughput;
        private set => SetProperty(ref _proxyOverviewThroughput, value);
    }

    public string ProxyOverviewLongStream
    {
        get => _proxyOverviewLongStream;
        private set => SetProperty(ref _proxyOverviewLongStream, value);
    }

    public string ProxyOverviewTraceability
    {
        get => _proxyOverviewTraceability;
        private set => SetProperty(ref _proxyOverviewTraceability, value);
    }

    public string ProxyOverviewBatch
    {
        get => _proxyOverviewBatch;
        private set => SetProperty(ref _proxyOverviewBatch, value);
    }

    private void LoadProxyAdvancedState(AppStateSnapshot snapshot)
    {
        _suppressProxyDiagnosticPresetSync = true;
        try
        {
            var hasKnownPreset = ProxyDiagnosticPresetOptions.Any(option =>
                string.Equals(option.Key, snapshot.ProxyDiagnosticPresetKey, StringComparison.OrdinalIgnoreCase));

            _selectedProxyDiagnosticPresetKey = ResolveProxyDiagnosticPresetKey(snapshot.ProxyDiagnosticPresetKey);
            _proxyEnableLongStreamingTest = snapshot.ProxyEnableLongStreamingTest;

            if (hasKnownPreset &&
                string.Equals(_selectedProxyDiagnosticPresetKey, "custom", StringComparison.OrdinalIgnoreCase))
            {
                _proxyEnableProtocolCompatibilityTest = snapshot.ProxyEnableProtocolCompatibilityTest;
                _proxyEnableErrorTransparencyTest = snapshot.ProxyEnableErrorTransparencyTest;
                _proxyEnableStreamingIntegrityTest = snapshot.ProxyEnableStreamingIntegrityTest;
                _proxyEnableMultiModalTest = snapshot.ProxyEnableMultiModalTest;
                _proxyEnableCacheMechanismTest = snapshot.ProxyEnableCacheMechanismTest;
                _proxyEnableCacheIsolationTest = snapshot.ProxyEnableCacheIsolationTest;
                _proxyEnableOfficialReferenceIntegrityTest = snapshot.ProxyEnableOfficialReferenceIntegrityTest;
            }
            else
            {
                _proxyEnableProtocolCompatibilityTest = true;
                _proxyEnableErrorTransparencyTest = true;
                _proxyEnableStreamingIntegrityTest = true;
                _proxyEnableMultiModalTest = true;
                _proxyEnableCacheMechanismTest = true;
                _proxyEnableCacheIsolationTest = false;
                _proxyEnableOfficialReferenceIntegrityTest = false;
            }

            _proxyCacheIsolationAlternateApiKey = snapshot.ProxyCacheIsolationAlternateApiKey ?? string.Empty;
            _proxyOfficialReferenceBaseUrl = snapshot.ProxyOfficialReferenceBaseUrl ?? string.Empty;
            _proxyOfficialReferenceApiKey = snapshot.ProxyOfficialReferenceApiKey ?? string.Empty;
            _proxyOfficialReferenceModel = snapshot.ProxyOfficialReferenceModel ?? string.Empty;
            _proxyBatchEnableLongStreamingTest = snapshot.ProxyBatchEnableLongStreamingTest;
            _proxyLongStreamSegmentsText = string.IsNullOrWhiteSpace(snapshot.ProxyLongStreamSegmentsText)
                ? "72"
                : snapshot.ProxyLongStreamSegmentsText;
        }
        finally
        {
            _suppressProxyDiagnosticPresetSync = false;
        }

        SyncProxyDiagnosticPresetFromFlags();
        OnPropertyChanged(nameof(SelectedProxyDiagnosticPresetKey));
        OnPropertyChanged(nameof(ProxyDiagnosticsPresetDescription));
        OnPropertyChanged(nameof(ProxyDiagnosticsExecutionSummary));
        OnPropertyChanged(nameof(ProxyEnableLongStreamingTest));
        OnPropertyChanged(nameof(ProxyEnableProtocolCompatibilityTest));
        OnPropertyChanged(nameof(ProxyEnableErrorTransparencyTest));
        OnPropertyChanged(nameof(ProxyEnableStreamingIntegrityTest));
        OnPropertyChanged(nameof(ProxyEnableMultiModalTest));
        OnPropertyChanged(nameof(ProxyEnableCacheMechanismTest));
        OnPropertyChanged(nameof(ProxyEnableCacheIsolationTest));
        OnPropertyChanged(nameof(ProxyCacheIsolationAlternateApiKey));
        OnPropertyChanged(nameof(ProxyEnableOfficialReferenceIntegrityTest));
        OnPropertyChanged(nameof(ProxyOfficialReferenceBaseUrl));
        OnPropertyChanged(nameof(ProxyOfficialReferenceApiKey));
        OnPropertyChanged(nameof(ProxyOfficialReferenceModel));
        OnPropertyChanged(nameof(ProxyBatchEnableLongStreamingTest));
        OnPropertyChanged(nameof(ProxyLongStreamSegmentsText));
        RefreshProxyOverviewSummary();
    }

    private void ApplyProxyAdvancedStateToSnapshot(AppStateSnapshot snapshot)
    {
        snapshot.ProxyDiagnosticPresetKey = SelectedProxyDiagnosticPresetKey;
        snapshot.ProxyEnableLongStreamingTest = ProxyEnableLongStreamingTest;
        snapshot.ProxyEnableProtocolCompatibilityTest = ProxyEnableProtocolCompatibilityTest;
        snapshot.ProxyEnableErrorTransparencyTest = ProxyEnableErrorTransparencyTest;
        snapshot.ProxyEnableStreamingIntegrityTest = ProxyEnableStreamingIntegrityTest;
        snapshot.ProxyEnableMultiModalTest = ProxyEnableMultiModalTest;
        snapshot.ProxyEnableCacheMechanismTest = ProxyEnableCacheMechanismTest;
        snapshot.ProxyEnableCacheIsolationTest = ProxyEnableCacheIsolationTest;
        snapshot.ProxyCacheIsolationAlternateApiKey = ProxyCacheIsolationAlternateApiKey;
        snapshot.ProxyEnableOfficialReferenceIntegrityTest = ProxyEnableOfficialReferenceIntegrityTest;
        snapshot.ProxyOfficialReferenceBaseUrl = ProxyOfficialReferenceBaseUrl;
        snapshot.ProxyOfficialReferenceApiKey = ProxyOfficialReferenceApiKey;
        snapshot.ProxyOfficialReferenceModel = ProxyOfficialReferenceModel;
        snapshot.ProxyBatchEnableLongStreamingTest = ProxyBatchEnableLongStreamingTest;
        snapshot.ProxyLongStreamSegmentsText = ProxyLongStreamSegmentsText;
    }

    private void ApplyProxyDiagnosticPreset(string presetKey)
    {
        if (_suppressProxyDiagnosticPresetSync)
        {
            return;
        }

        _suppressProxyDiagnosticPresetSync = true;
        try
        {
            if (string.Equals(presetKey, "deep", StringComparison.OrdinalIgnoreCase))
            {
                ProxyEnableProtocolCompatibilityTest = true;
                ProxyEnableErrorTransparencyTest = true;
                ProxyEnableStreamingIntegrityTest = true;
                ProxyEnableMultiModalTest = true;
                ProxyEnableCacheMechanismTest = true;
                ProxyEnableCacheIsolationTest = false;
                ProxyEnableOfficialReferenceIntegrityTest = false;
            }
        }
        finally
        {
            _suppressProxyDiagnosticPresetSync = false;
        }

        RefreshProxyOverviewSummary();
    }

    private void SyncProxyDiagnosticPresetFromFlags()
    {
        if (_suppressProxyDiagnosticPresetSync)
        {
            return;
        }

        var derivedKey =
            ProxyEnableProtocolCompatibilityTest &&
            ProxyEnableErrorTransparencyTest &&
            ProxyEnableStreamingIntegrityTest &&
            ProxyEnableMultiModalTest &&
            ProxyEnableCacheMechanismTest &&
            !ProxyEnableCacheIsolationTest &&
            !ProxyEnableOfficialReferenceIntegrityTest
                ? "deep"
                : "custom";

        if (string.Equals(_selectedProxyDiagnosticPresetKey, derivedKey, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _selectedProxyDiagnosticPresetKey = derivedKey;
        OnPropertyChanged(nameof(SelectedProxyDiagnosticPresetKey));
        OnPropertyChanged(nameof(ProxyDiagnosticsPresetDescription));
        OnPropertyChanged(nameof(ProxyDiagnosticsExecutionSummary));
    }

    private string ResolveProxyDiagnosticPresetKey(string? requestedKey)
        => ProxyDiagnosticPresetOptions
            .FirstOrDefault(option => string.Equals(option.Key, requestedKey, StringComparison.OrdinalIgnoreCase))?.Key
           ?? "deep";

    private static string GetProxyDiagnosticPresetDisplayName(string presetKey)
        => presetKey switch
        {
            "custom" => "自定义",
            _ => "标准深测"
        };

    private int GetProxyLongStreamSegmentCount()
        => ParseBoundedInt(ProxyLongStreamSegmentsText, fallback: 72, min: 24, max: 240);

    private string DescribeCacheIsolationExecutionState()
    {
        if (!ProxyEnableCacheIsolationTest)
        {
            return "关闭";
        }

        if (string.IsNullOrWhiteSpace(ProxyCacheIsolationAlternateApiKey))
        {
            return "开启（待填 B Key）";
        }

        if (string.Equals(ProxyApiKey?.Trim(), ProxyCacheIsolationAlternateApiKey.Trim(), StringComparison.Ordinal))
        {
            return "开启（B Key 与主 Key 相同）";
        }

        return "开启（A/B Key 已配置）";
    }

    private string DescribeOfficialReferenceExecutionState()
    {
        if (!ProxyEnableOfficialReferenceIntegrityTest)
        {
            return "关闭";
        }

        if (string.IsNullOrWhiteSpace(ProxyOfficialReferenceBaseUrl) ||
            string.IsNullOrWhiteSpace(ProxyOfficialReferenceApiKey))
        {
            return "开启（待填官方 Base URL / Key）";
        }

        if (string.IsNullOrWhiteSpace(ProxyOfficialReferenceModel))
        {
            return "开启（模型沿用当前填写值）";
        }

        return "开启（官方参考已配置）";
    }

    private void RefreshProxyAdvancedSummaries(ProxyDiagnosticsResult result)
    {
        if (result.LongStreamingResult is { } longStreamingResult)
        {
            ProxyLongStreamingSummary =
                $"长流稳定性：{(longStreamingResult.Success ? "通过" : "异常")}\n" +
                $"段数：{longStreamingResult.ActualSegmentCount}/{longStreamingResult.ExpectedSegmentCount}\n" +
                $"DONE：{(longStreamingResult.ReceivedDone ? "已收到" : "缺失")}\n" +
                $"输出速率：{FormatTokensPerSecond(longStreamingResult.OutputTokensPerSecond, longStreamingResult.OutputTokenCountEstimated)}\n" +
                $"最大 chunk 间隔：{FormatMillisecondsDoubleValue(longStreamingResult.MaxChunkGapMilliseconds)}\n" +
                $"摘要：{longStreamingResult.Summary}";
        }
        else
        {
            ProxyLongStreamingSummary = ProxyEnableLongStreamingTest
                ? "长流稳定简测已启用，但本次还没有执行结果。"
                : "长流稳定简测未启用。";
        }

        ProxyTraceabilitySummary =
            $"可追溯性：{result.TraceabilitySummary ?? "未识别"}\n" +
            $"Request-ID：{(string.IsNullOrWhiteSpace(result.RequestId) ? "--" : result.RequestId)}\n" +
            $"Trace-ID：{(string.IsNullOrWhiteSpace(result.TraceId) ? "--" : result.TraceId)}";

        if (result.ThroughputBenchmarkResult is { } throughputBenchmark)
        {
            ProxyLongStreamingSummary +=
                $"\n\n独立吞吐测试：{BuildThroughputBenchmarkDigest(throughputBenchmark)}";
        }

        RefreshProxyOverviewSummary();
    }

    private void RefreshProxyOverviewSummary()
    {
        var preferredSingleResult = _lastProxySingleResult;
        var preferredStabilityResult = _lastProxyStabilityResult?.RoundResults
            .OrderByDescending(item => item.CheckedAt)
            .FirstOrDefault();
        var preferredBatchResult = OrderBatchAggregateRows(BuildProxyBatchAggregateRows(_proxyBatchChartRuns))
            .FirstOrDefault()?
            .LatestResult;
        var referenceResult = preferredSingleResult ?? preferredStabilityResult ?? preferredBatchResult;

        ProxyOverviewVerdict = referenceResult is null
            ? "等待单站或批量结果"
            : referenceResult.Verdict ?? "待复核";
        ProxyOverviewLatency = referenceResult is null
            ? "普通 -- / TTFT --"
            : $"普通 {FormatMilliseconds(referenceResult.ChatLatency)} / TTFT {FormatMilliseconds(referenceResult.StreamFirstTokenLatency)}";

        var throughputBenchmark = referenceResult?.ThroughputBenchmarkResult;
        var streamScenario = referenceResult is null
            ? null
            : FindScenario(GetScenarioResults(referenceResult), ProxyProbeScenarioKind.ChatCompletionsStream);
        ProxyOverviewThroughput = throughputBenchmark is not null
            ? $"独立吞吐 {FormatTokensPerSecond(throughputBenchmark.MedianOutputTokensPerSecond, throughputBenchmark.OutputTokenCountEstimated, throughputBenchmark.CompletedSampleCount)} / 区间 {FormatThroughputBenchmarkRange(throughputBenchmark)}"
            : streamScenario is null
                ? "独立吞吐 --"
                : $"流式探针 {FormatTokensPerSecond(streamScenario.OutputTokensPerSecond, streamScenario.OutputTokenCountEstimated, streamScenario.OutputTokensPerSecondSampleCount)} / 输出 {streamScenario.OutputTokenCount?.ToString() ?? "--"}";

        var longStreamingResult = preferredSingleResult?.LongStreamingResult ?? preferredBatchResult?.LongStreamingResult;
        ProxyOverviewLongStream = longStreamingResult is null
            ? (ProxyEnableLongStreamingTest || ProxyBatchEnableLongStreamingTest ? "未运行" : "未启用")
            : $"{(longStreamingResult.Success ? "通过" : "异常")} / {longStreamingResult.ActualSegmentCount}/{longStreamingResult.ExpectedSegmentCount}";

        ProxyOverviewTraceability = referenceResult?.TraceabilitySummary ?? "等待诊断";

        var batchRows = OrderBatchAggregateRows(BuildProxyBatchAggregateRows(_proxyBatchChartRuns)).ToArray();
        if (batchRows.Length == 0)
        {
            ProxyOverviewBatch = "暂无入口组推荐";
            return;
        }

        var best = batchRows[0];
        ProxyOverviewBatch =
            $"推荐 {best.Entry.Name}\n" +
            $"{FormatMillisecondsDoubleValue(best.AverageChatLatencyMs)} / {FormatTokensPerSecond(best.AverageBenchmarkTokensPerSecond)} / 综合分 {best.CompositeScore:F1}";
    }

    private static string FormatTokensPerSecond(double? value, bool estimated = false, int sampleCount = 1)
    {
        if (!value.HasValue)
        {
            return "--";
        }

        var suffix = sampleCount > 1
            ? estimated
                ? $"（{sampleCount}次均值，估算）"
                : $"（{sampleCount}次均值）"
            : estimated
                ? "（估算）"
                : string.Empty;
        return $"{value.Value:F1} tok/s{suffix}";
    }

    private static string BuildThroughputBenchmarkDigest(ProxyThroughputBenchmarkResult? result)
    {
        if (result is null)
        {
            return "未运行";
        }

        return $"{result.SuccessfulSampleCount}/{result.CompletedSampleCount} 轮成功 / 中位数 {FormatTokensPerSecond(result.MedianOutputTokensPerSecond, result.OutputTokenCountEstimated, result.CompletedSampleCount)} / 区间 {FormatThroughputBenchmarkRange(result)}";
    }

    private static string FormatThroughputBenchmarkRange(ProxyThroughputBenchmarkResult? result)
    {
        if (result?.MinimumOutputTokensPerSecond is not double minimum ||
            result.MaximumOutputTokensPerSecond is not double maximum)
        {
            return "--";
        }

        return $"{minimum:F1}-{maximum:F1} tok/s";
    }

    private static string FormatMillisecondsDoubleValue(double? value)
        => value.HasValue ? $"{value.Value:F0} ms" : "--";
}
