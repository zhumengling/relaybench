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

public sealed partial class SingleStationViewModel
{    // ========== Quick Mode ==========
    private async Task RunQuickModeAsync(CancellationToken ct)
    {
        StatusText = "正在运行快速诊断...";
        HasQuickResults = false;
        ResetThroughputSamplingBuffers();
        var settings = BuildSettings();
        var liveProgress = new Progress<ProxyDiagnosticsLiveProgress>(ApplyProxyLiveProgressSafe);
        var result = await _diagnosticsService.RunAsync(settings, liveProgress, ct);
        ApplyCommonDiagnosticsDetails(result, "Quick baseline");
        BuildQuickModeCharts(result);
        HasQuickResults = true;
        UpdateKpiLabels(SelectedTestMode);
        StatusText = "基准诊断完成，正在运行独立吞吐测试...";
        ct.ThrowIfCancellationRequested();

        StatusText = "正在运行独立吞吐测试...";
        var throughputSettings = string.IsNullOrWhiteSpace(result.EffectiveModel)
            ? settings
            : settings with { Model = result.EffectiveModel.Trim() };
        var throughputBenchmark = await _diagnosticsService.RunThroughputBenchmarkAsync(
            throughputSettings,
            baselineResult: result,
            liveProgress: new Progress<ProxyThroughputBenchmarkLiveProgress>(ApplyThroughputLiveProgressSafe),
            cancellationToken: ct);
        result = result with { ThroughputBenchmarkResult = throughputBenchmark };

        var totalMs = (result.ModelsLatency?.TotalMilliseconds ?? 0)
                    + (result.ChatLatency?.TotalMilliseconds ?? 0)
                    + (result.StreamDuration?.TotalMilliseconds ?? 0);
        TotalTime = totalMs > 0 ? $"{totalMs / 1000.0:F2}s" : "0s";

        ModelsLatency = result.ModelsLatency.HasValue
            ? $"{result.ModelsLatency.Value.TotalMilliseconds:F0} ms" : "0 ms";
        ChatLatency = result.ChatLatency.HasValue
            ? $"{result.ChatLatency.Value.TotalMilliseconds:F0} ms" : "0 ms";
        StreamTtft = result.StreamFirstTokenLatency.HasValue
            ? $"{result.StreamFirstTokenLatency.Value.TotalMilliseconds:F0} ms" : "0 ms";
        ChatStatusCode = result.ChatStatusCode.HasValue
            ? $"{result.ChatStatusCode}" : "0";
        ModelCount = $"{result.ModelCount}";
        ChatPreview = result.ChatPreview ?? "";
        Verdict = result.Verdict ?? (result.ChatRequestSucceeded ? "Pass" : "Fail");
        VerdictReason = result.Summary ?? "";
        CapabilitySummary = result.ChatRequestSucceeded ? "\u5168\u90E8\u901A\u8FC7" : "\u9700\u590D\u6838";

        var outputTokens = TokenCountEstimator.EstimateOutputTokens(result.ChatPreview ?? result.StreamPreview);

        // Stats row extras
        QuickRequestSize = "0 tokens";
        QuickResponseSize = outputTokens > 0 ? $"{outputTokens:N0} tokens" : "0 tokens";
        QuickSuccessRate = result.ChatRequestSucceeded ? "100.00%" : "0%";
        SuccessRateDetail = result.ChatRequestSucceeded ? "10/10" : "0/10";
        ErrorRateDisplay = result.ChatRequestSucceeded ? "0%" : "100%";
        QuickAvgLatency = result.ChatLatency.HasValue
            ? $"{result.ChatLatency.Value.TotalMilliseconds:F0} ms" : "0 ms";
        var tps = result.ThroughputBenchmarkResult?.MedianOutputTokensPerSecond;
        QuickThroughput = tps.HasValue
            ? FormatTokensPerSecond(tps) : "0 tok/s";

        ChatSupported = result.ScenarioResults?
            .Any(s => s.Scenario == ProxyProbeScenarioKind.ChatCompletions && s.Success) == true;
        ResponsesSupported = result.ScenarioResults?
            .Any(s => s.Scenario == ProxyProbeScenarioKind.Responses && s.Success) == true;
        AnthropicSupported = result.ScenarioResults?
            .Any(s => s.Scenario == ProxyProbeScenarioKind.AnthropicMessages && s.Success) == true;

        ProtocolPreferred = ResponsesSupported ? "Responses API"
            : AnthropicSupported ? "Anthropic"
            : ChatSupported ? "Chat" : "0";

        if (ResponsesSupported)
            ProtocolDetection = new ProtocolDetectionResult("OpenAI Responses", "2025-03-11");
        else if (AnthropicSupported)
            ProtocolDetection = new ProtocolDetectionResult("Anthropic Messages", "2023-06-01");
        else if (ChatSupported)
            ProtocolDetection = new ProtocolDetectionResult("OpenAI Chat", "v1");
        else
            ProtocolDetection = ProtocolDetectionResult.Unknown;

        ProbeResultSummary = result.Summary ?? "";
        ApplyProtocolCapabilityStates(result);

        // Build Quick mode charts with sample data from the single run
        BuildQuickModeCharts(result);
        HasQuickResults = true;
        UpdateKpiLabels(SelectedTestMode);

        StatusText = result.Error is null
            ? "\u6D4B\u8BD5\u5DF2\u5B8C\u6210"
            : $"已完成: {result.Error}";

        // Populate new UI fields
        TestTimestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        EntryNodeName = ExtractHostFromUrl(BaseUrl);
        RawResponseJson = BuildResponseJson(result);
        ResponseHeaders = result.ResponseHeadersSummary ?? "content-type: application/json";
        TraceTimings = $"总耗时: {TotalTime}\n聊天请求: {ChatLatency}\nTTFT: {StreamTtft}\n模型列表: {ModelsLatency}";
        TestLog = $"[{DateTime.Now:HH:mm:ss}] 测试开始\n[{DateTime.Now:HH:mm:ss}] 快速模式完成\n[{DateTime.Now:HH:mm:ss}] 结论: {VerdictDisplay}";
        ProtocolVersion = ProtocolDetection.Version ?? "0";
        ResponseContentType = "application/json";
        CompletionReason = result.ChatRequestSucceeded ? "stop" : "0";
    }

    private static string ExtractHostFromUrl(string url)
    {
        try { return new Uri(url).Host; }
        catch { return url; }
    }

    private void BuildQuickModeCharts(ProxyDiagnosticsResult result)
    {
        var theme = GetChartTheme();

        var latencyValues = ExtractQuickLatencySamples(result);
        var hasRealLatencySamples = latencyValues.Length > 0;
        if (!hasRealLatencySamples)
        {
            latencyValues = [0d];
        }

        BuildQuickLatencyChart(latencyValues);
        LatencyTooltipTime = hasRealLatencySamples
            ? $"\u66F4\u65B0 {DateTime.Now:HH:mm:ss}"
            : "\u6682\u65E0\u771F\u5B9E\u6837\u672C";
        LatencyTooltipP50 = hasRealLatencySamples
            ? $"\u6700\u5C0F: {latencyValues.Min():N0} ms"
            : "最小: 0 ms";
        LatencyTooltipP95 = hasRealLatencySamples
            ? $"\u5E73\u5747: {latencyValues.Average():N0} ms"
            : "平均: 0 ms";
        LatencyTooltipP99 = hasRealLatencySamples
            ? $"\u6700\u5927: {latencyValues.Max():N0} ms"
            : "最大: 0 ms";

        var ttftValues = ExtractTtftSamples(result);
        var (ttftSeries, _) = TtftDistributionChartBuilder.Build(ttftValues, theme);
        QuickTtftChartSeries = ttftSeries;
        QuickTtftChartYAxes = TtftDistributionChartBuilder.BuildYAxes(theme);
        QuickTtftChartXAxes = TtftDistributionChartBuilder.BuildXAxes(theme);

        // Throughput charts use completed benchmark samples when available.
        var generationSamples = ExtractOutputThroughputSamples(result.ThroughputBenchmarkResult);
        var endToEndSamples = ExtractEndToEndThroughputSamples(result.ThroughputBenchmarkResult, generationSamples);
        BuildThroughputChartFromSamples(generationSamples);
        BuildStreamingTokenChart(endToEndSamples, generationSamples);
    }

    private void BuildQuickLatencyChart(IReadOnlyList<double> latencyValues)
    {
        LiveChartsInitializer.EnsureInitialized();
        var theme = GetChartTheme();
        var colors = ChartPalette.ForTheme(theme);
        var labelPaint = ChartPalette.LegendPaint(theme);
        var values = latencyValues.Count > 0 ? latencyValues.ToArray() : [0d];

        QuickLatencyChartSeries =
        [
            new LineSeries<double>
            {
                Values = values,
                Name = "\u771F\u5B9E\u91C7\u6837",
                GeometrySize = 0,
                LineSmoothness = 0.35,
                Stroke = new SolidColorPaint(colors[0]) { StrokeThickness = 2 },
                Fill = new SolidColorPaint(colors[0].WithAlpha(28)),
            },
        ];

        QuickLatencyChartYAxes =
        [
            new Axis
            {
                Name = "\u5EF6\u8FDF (ms)",
                NamePaint = labelPaint,
                LabelsPaint = labelPaint,
            },
        ];

        QuickLatencyChartXAxes =
        [
            new Axis
            {
                Name = "\u6837\u672C\u5E8F\u53F7",
                Labels = BuildSampleLabels(values.Length),
                NamePaint = labelPaint,
                LabelsPaint = labelPaint,
            },
        ];
    }

    private void BuildStreamingTokenChart(IReadOnlyList<double> endToEndRates, IReadOnlyList<double> generationRates)
    {
        LiveChartsInitializer.EnsureInitialized();
        var theme = GetChartTheme();
        var labelPaint = ChartPalette.LegendPaint(theme);
        var endToEndColor = new SKColor(0x2F, 0x7D, 0xFF);
        var generationColor = new SKColor(0x18, 0xB6, 0xA6);
        var inputRates = endToEndRates.Count > 0 ? endToEndRates.ToArray() : [0d];
        var outputRates = generationRates.Count > 0 ? generationRates.ToArray() : [0d];

        var averageEndToEnd = AverageOrNull(inputRates);
        var averageGeneration = AverageOrNull(outputRates);
        StreamingInputLabel = $"端到端均值 ({FormatCompactTokensPerSecond(averageEndToEnd)})";
        StreamingOutputLabel = $"生成均值 ({FormatCompactTokensPerSecond(averageGeneration)})";

        StreamingTokenChartSeries =
        [
            new LineSeries<double>
            {
                Values = inputRates,
                Name = StreamingInputLabel,
                GeometrySize = 0,
                LineSmoothness = 0.55,
                Stroke = new SolidColorPaint(endToEndColor) { StrokeThickness = 2 },
                Fill = new SolidColorPaint(endToEndColor.WithAlpha(32)),
            },
            new LineSeries<double>
            {
                Values = outputRates,
                Name = StreamingOutputLabel,
                GeometrySize = 0,
                LineSmoothness = 0.55,
                Stroke = new SolidColorPaint(generationColor) { StrokeThickness = 2 },
                Fill = new SolidColorPaint(generationColor.WithAlpha(42)),
            },
        ];

        StreamingTokenChartYAxes =
        [
            new Axis
            {
                Name = "tokens/s",
                NamePaint = labelPaint,
                LabelsPaint = labelPaint,
            },
        ];

        StreamingTokenChartXAxes =
        [
            new Axis
            {
                Labels = BuildSampleLabels(Math.Max(inputRates.Length, outputRates.Length)),
                LabelsPaint = labelPaint,
            },
        ];
    }

    private void BuildThroughputChartFromSamples(IReadOnlyList<double> tokensPerSec)
    {
        LiveChartsInitializer.EnsureInitialized();
        var theme = GetChartTheme();
        var labelPaint = ChartPalette.LegendPaint(theme);
        var instantColor = new SKColor(0x2F, 0x7D, 0xFF);
        var smoothColor = new SKColor(0x18, 0xB6, 0xA6);
        var values = tokensPerSec.Count > 0 ? tokensPerSec.ToArray() : [0d];
        var movingAverage = BuildMovingAverage(values, windowSize: 5);

        QuickThroughputChartSeries =
        [
            new LineSeries<double>
            {
                Values = values,
                Name = "采样吞吐",
                GeometrySize = 0,
                LineSmoothness = 0.35,
                Stroke = new SolidColorPaint(instantColor) { StrokeThickness = 2 },
                Fill = new SolidColorPaint(instantColor.WithAlpha(30)),
            },
            new LineSeries<double>
            {
                Values = movingAverage,
                Name = "平滑均值",
                GeometrySize = 0,
                LineSmoothness = 0.5,
                Stroke = new SolidColorPaint(smoothColor) { StrokeThickness = 2 },
                Fill = null,
            },
        ];

        QuickThroughputChartYAxes =
        [
            new Axis
            {
                Name = "tokens/s",
                NamePaint = labelPaint,
                LabelsPaint = labelPaint,
            },
        ];

        QuickThroughputChartXAxes =
        [
            new Axis
            {
                Name = "采样点",
                Labels = BuildSampleLabels(values.Length),
                NamePaint = labelPaint,
                LabelsPaint = labelPaint,
            },
        ];
    }

    private static ElementTheme GetChartTheme()
        => ChartPalette.ResolveTheme(ThemeService.GetCurrentTheme());

    private void ApplyProtocolCapabilityStates(ProxyDiagnosticsResult result)
    {
        SetCapabilityState(0, result.ModelsRequestSucceeded ? CapabilityState.Supported : CapabilityState.Unsupported,
            ModelsLatency, ModelCount);
        SetCapabilityState(1, ChatSupported ? CapabilityState.Supported : CapabilityState.Unsupported,
            StreamTtft, result.StreamRequestSucceeded ? "\u901A\u8FC7" : "\u8B66\u544A");
        SetCapabilityState(2, ResponsesSupported ? CapabilityState.Supported : CapabilityState.Unknown,
            ResponsesSupported ? StreamTtft : "0 ms", ResponsesSupported ? "\u901A\u8FC7" : "0");
        SetCapabilityState(3, AnthropicSupported ? CapabilityState.Supported : CapabilityState.Unknown,
            AnthropicSupported ? StreamTtft : "0 ms", AnthropicSupported ? "\u901A\u8FC7" : "0");

        SetCapabilityState(4, HasScenario(result, ProxyProbeScenarioKind.StructuredOutputEdge) || HasScenario(result, ProxyProbeScenarioKind.StructuredOutput)
            ? CapabilityState.Supported : CapabilityState.Unknown);
        SetCapabilityState(5, HasScenario(result, ProxyProbeScenarioKind.FunctionCalling) || HasScenario(result, ProxyProbeScenarioKind.ToolCallDeep)
            ? CapabilityState.Supported : CapabilityState.Unknown);
        SetCapabilityState(6, HasScenario(result, ProxyProbeScenarioKind.ErrorTransparency)
            ? CapabilityState.Supported : CapabilityState.Unknown);
        SetCapabilityState(7, HasScenario(result, ProxyProbeScenarioKind.MultiModal)
            ? CapabilityState.Supported : CapabilityState.Unknown);
    }

    private void SetCapabilityState(int index, CapabilityState state, string? primary = null, string? secondary = null)
    {
        if (index < 0 || index >= Capabilities.Count)
        {
            return;
        }

        var current = Capabilities[index];
        Capabilities[index] = current with
        {
            State = state,
            PrimaryMetricValue = primary ?? current.PrimaryMetricValue,
            SecondaryMetricValue = secondary ?? current.SecondaryMetricValue
        };
    }

    private static bool HasScenario(ProxyDiagnosticsResult result, ProxyProbeScenarioKind scenario)
        => result.ScenarioResults?.Any(s => s.Scenario == scenario && s.Success) == true;

    private void ApplyCommonDiagnosticsDetails(ProxyDiagnosticsResult result, string modeName)
    {
        var totalMs = (result.ModelsLatency?.TotalMilliseconds ?? 0)
                    + (result.ChatLatency?.TotalMilliseconds ?? 0)
                    + (result.StreamDuration?.TotalMilliseconds ?? 0);
        TotalTime = totalMs > 0 ? $"{totalMs / 1000.0:F2}s" : "0s";
        ModelsLatency = result.ModelsLatency.HasValue
            ? $"{result.ModelsLatency.Value.TotalMilliseconds:F0} ms" : "0 ms";
        ChatLatency = result.ChatLatency.HasValue
            ? $"{result.ChatLatency.Value.TotalMilliseconds:F0} ms" : "0 ms";
        StreamTtft = result.StreamFirstTokenLatency.HasValue
            ? $"{result.StreamFirstTokenLatency.Value.TotalMilliseconds:F0} ms" : "0 ms";
        ChatStatusCode = result.ChatStatusCode.HasValue ? $"{result.ChatStatusCode}" : "0";
        ModelCount = $"{result.ModelCount}";
        ChatPreview = result.ChatPreview ?? result.StreamPreview ?? "";
        Verdict = result.Verdict ?? (result.ChatRequestSucceeded ? "Pass" : "Fail");
        VerdictReason = result.PrimaryIssue ?? result.Summary ?? "";
        CapabilitySummary = BuildCapabilitySummary(result);

        var outputTokens = TokenCountEstimator.EstimateOutputTokens(result.ChatPreview ?? result.StreamPreview);
        QuickResponseSize = outputTokens > 0 ? $"{outputTokens:N0} tokens" : "0 tokens";
        QuickSuccessRate = result.ChatRequestSucceeded ? "100.00%" : "0%";
        SuccessRateDetail = result.ChatRequestSucceeded ? "基准通过" : "基准失败";
        ErrorRateDisplay = result.ChatRequestSucceeded ? "0%" : "100%";
        QuickAvgLatency = ChatLatency;
        QuickThroughput = FormatTokensPerSecond(result.ThroughputBenchmarkResult?.MedianOutputTokensPerSecond);

        ChatSupported = result.ScenarioResults?
            .Any(s => s.Scenario == ProxyProbeScenarioKind.ChatCompletions && s.Success) == true;
        ResponsesSupported = result.ScenarioResults?
            .Any(s => s.Scenario == ProxyProbeScenarioKind.Responses && s.Success) == true;
        AnthropicSupported = result.ScenarioResults?
            .Any(s => s.Scenario == ProxyProbeScenarioKind.AnthropicMessages && s.Success) == true;
        ProtocolPreferred = ResponsesSupported ? "Responses API"
            : AnthropicSupported ? "Anthropic"
            : ChatSupported ? "Chat" : "0";
        ProtocolDetection = ResponsesSupported
            ? new ProtocolDetectionResult("OpenAI Responses", "2025-03-11")
            : AnthropicSupported
                ? new ProtocolDetectionResult("Anthropic Messages", "2023-06-01")
                : ChatSupported
                    ? new ProtocolDetectionResult("OpenAI Chat", "v1")
                    : ProtocolDetectionResult.Unknown;

        ApplyProtocolCapabilityStates(result);
        ApplyNonChatCapabilityStates(result);
        TestTimestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        EntryNodeName = ExtractHostFromUrl(result.BaseUrl);
        RawResponseJson = BuildResponseJson(result);
        ResponseHeaders = result.ResponseHeadersSummary ?? "content-type: application/json";
        TraceTimings = BuildTraceTimings(result);
        TestLog = BuildTestLog(result, modeName);
        ProtocolVersion = ProtocolDetection.Version ?? "0";
        ResponseContentType = ResolveContentType(result);
        CompletionReason = result.ChatRequestSucceeded ? "stop" : "0";
    }

    private void ApplyProxyLiveProgressSafe(ProxyDiagnosticsLiveProgress progress)
    {
        try
        {
            ApplyProxyLiveProgress(progress);
        }
        catch (Exception ex)
        {
            RelayBench.Services.Infrastructure.AppDiagnosticLog.Write("SingleStation.ProxyLiveProgress", ex);
            StatusText = "实时进度刷新失败，测试仍在继续";
        }
    }

    private void ApplyThroughputLiveProgressSafe(ProxyThroughputBenchmarkLiveProgress progress)
    {
        try
        {
            ApplyThroughputLiveProgress(progress);
        }
        catch (Exception ex)
        {
            RelayBench.Services.Infrastructure.AppDiagnosticLog.Write("SingleStation.ThroughputLiveProgress", ex);
            StatusText = "吞吐图表刷新失败，测试仍在继续";
        }
    }

    private void ApplyProxyLiveProgress(ProxyDiagnosticsLiveProgress progress)
    {
        var scenario = progress.CurrentScenarioResult;
        StatusText = $"{progress.CompletedScenarioCount}/{progress.TotalScenarioCount} {scenario.DisplayName}: {scenario.CapabilityStatus}";
        ModelCount = progress.ModelCount > 0 ? $"{progress.ModelCount}" : ModelCount;

        UpsertDeepScenario(scenario);
        ApplyScenarioCapabilityState(scenario);

        var passed = progress.ScenarioResults.Count(static item => item.Success);
        var failed = progress.ScenarioResults.Count(static item => !item.Success);
        DeepPassCount = $"{passed}";
        DeepFailCount = $"{failed}";
        DeepSummary = $"已完成 {progress.CompletedScenarioCount}/{progress.TotalScenarioCount} 个场景";
        CapabilitySummary = $"{passed}/{Math.Max(progress.ScenarioResults.Count, 1)} 通过";
        TestTimestamp = progress.ReportedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
        EntryNodeName = ExtractHostFromUrl(progress.BaseUrl);
        if (!string.IsNullOrWhiteSpace(progress.EffectiveModel))
        {
            ProtocolVersion = progress.EffectiveModel;
        }

        TestLog = AppendLogLine(TestLog, $"[{DateTime.Now:HH:mm:ss}] {scenario.DisplayName}: {scenario.CapabilityStatus} - {scenario.Summary}");
        UpdateKpiLabels(SelectedTestMode);
    }

    private void ApplyThroughputLiveProgress(ProxyThroughputBenchmarkLiveProgress progress)
    {
        var liveThroughput = progress.CurrentOutputTokensPerSecond
            ?? progress.LiveMedianOutputTokensPerSecond
            ?? progress.LiveAverageOutputTokensPerSecond;
        QuickThroughput = FormatTokensPerSecond(liveThroughput);
        StatusText = progress.Summary;

        if (ShouldSampleThroughputChart(progress, liveThroughput))
        {
            AppendThroughputLiveSample(progress, liveThroughput);
            BuildThroughputChartFromSamples(_throughputChartSamples);
            BuildStreamingTokenChart(_streamingInputRateSamples, _streamingOutputRateSamples);
        }

        if (DateTimeOffset.Now - _lastThroughputLogAt >= ThroughputLogInterval ||
            progress.CompletedSampleCount >= progress.RequestedSampleCount)
        {
            _lastThroughputLogAt = DateTimeOffset.Now;
            TestLog = AppendLogLine(TestLog, $"[{DateTime.Now:HH:mm:ss}] 吞吐采样 {progress.CompletedSampleCount}/{progress.RequestedSampleCount}: {QuickThroughput}");
        }

        UpdateKpiLabels(SelectedTestMode);
    }

    private bool ShouldSampleThroughputChart(
        ProxyThroughputBenchmarkLiveProgress progress,
        double? liveThroughput)
    {
        if (!liveThroughput.HasValue &&
            !progress.LiveAverageOutputTokensPerSecond.HasValue &&
            !progress.LiveMedianOutputTokensPerSecond.HasValue &&
            !progress.LiveMaximumOutputTokensPerSecond.HasValue)
        {
            return false;
        }

        var now = DateTimeOffset.Now;
        var completedChanged = progress.CompletedSampleCount != _lastThroughputCompletedSampleCount;
        var benchmarkCompleted = progress.CompletedSampleCount >= progress.RequestedSampleCount &&
                                 progress.RequestedSampleCount > 0;
        if (!completedChanged &&
            !benchmarkCompleted &&
            now - _lastThroughputChartRefresh < ThroughputChartRefreshInterval)
        {
            return false;
        }

        _lastThroughputCompletedSampleCount = progress.CompletedSampleCount;
        _lastThroughputChartRefresh = now;
        return true;
    }

    private void AppendThroughputLiveSample(
        ProxyThroughputBenchmarkLiveProgress progress,
        double? liveThroughput)
    {
        var outputRate = liveThroughput
            ?? progress.LiveMedianOutputTokensPerSecond
            ?? progress.LiveAverageOutputTokensPerSecond
            ?? progress.LiveMaximumOutputTokensPerSecond;
        if (!TryNormalizeThroughputChartSample(outputRate, out var normalizedOutputRate))
        {
            return;
        }

        var inputRate = progress.CurrentEndToEndTokensPerSecond
            ?? progress.LiveAverageOutputTokensPerSecond
            ?? normalizedOutputRate;
        if (!TryNormalizeThroughputChartSample(inputRate, out var normalizedInputRate))
        {
            normalizedInputRate = normalizedOutputRate;
        }

        AppendCapped(_throughputChartSamples, normalizedOutputRate, ThroughputChartSampleLimit);
        AppendCapped(_streamingOutputRateSamples, normalizedOutputRate, ThroughputChartSampleLimit);
        AppendCapped(_streamingInputRateSamples, normalizedInputRate, ThroughputChartSampleLimit);
    }

    private void ResetThroughputSamplingBuffers()
    {
        _throughputChartSamples.Clear();
        _streamingInputRateSamples.Clear();
        _streamingOutputRateSamples.Clear();
        _lastThroughputChartRefresh = DateTimeOffset.MinValue;
        _lastThroughputLogAt = DateTimeOffset.MinValue;
        _lastThroughputCompletedSampleCount = -1;
    }

    private static void AppendCapped(List<double> values, double value, int limit)
    {
        values.Add(value);
        while (values.Count > limit)
        {
            values.RemoveAt(0);
        }
    }

    private static bool TryNormalizeThroughputChartSample(double? value, out double normalized)
    {
        normalized = 0d;
        if (!value.HasValue ||
            double.IsNaN(value.Value) ||
            double.IsInfinity(value.Value) ||
            value.Value <= 0d ||
            value.Value > MaximumDisplayedTokensPerSecond)
        {
            return false;
        }

        normalized = value.Value;
        return true;
    }

    private static double[] BuildMovingAverage(IReadOnlyList<double> values, int windowSize)
    {
        if (values.Count == 0)
        {
            return [];
        }

        var normalizedWindow = Math.Max(1, windowSize);
        var result = new double[values.Count];
        for (var i = 0; i < values.Count; i++)
        {
            var start = Math.Max(0, i - normalizedWindow + 1);
            var count = i - start + 1;
            var sum = 0d;
            for (var j = start; j <= i; j++)
            {
                sum += values[j];
            }

            result[i] = sum / count;
        }

        return result;
    }

    private static string[] BuildSampleLabels(int count)
        => Enumerable.Range(1, Math.Max(0, count))
            .Select(static index => $"{index}")
            .ToArray();

    private static double[] ExtractQuickLatencySamples(ProxyDiagnosticsResult result)
    {
        var values = new List<double>();
        if (result.ScenarioResults is { Count: > 0 } scenarios)
        {
            values.AddRange(scenarios
                .Select(static scenario => scenario.Latency?.TotalMilliseconds)
                .Where(static value => value is > 0)
                .Select(static value => value!.Value));
            if (values.Count > 0)
            {
                return values.ToArray();
            }
        }

        AddPositiveSample(values, result.ModelsLatency?.TotalMilliseconds);
        AddPositiveSample(values, result.ChatLatency?.TotalMilliseconds);
        AddPositiveSample(values, result.StreamFirstTokenLatency?.TotalMilliseconds);
        AddPositiveSample(values, result.StreamDuration?.TotalMilliseconds);

        return values.Count > 0 ? values.ToArray() : [];
    }

    private static double[] ExtractTtftSamples(ProxyDiagnosticsResult result)
    {
        var values = new List<double>();
        if (result.ScenarioResults is { Count: > 0 } scenarios)
        {
            values.AddRange(scenarios
                .Select(static scenario => scenario.FirstTokenLatency?.TotalMilliseconds)
                .Where(static value => value is > 0)
                .Select(static value => value!.Value));
            if (values.Count > 0)
            {
                return values.ToArray();
            }
        }

        if (result.ThroughputBenchmarkResult?.Samples is { Count: > 0 } samples)
        {
            values.AddRange(samples
                .Select(static sample => sample.FirstTokenLatency?.TotalMilliseconds)
                .Where(static value => value is > 0)
                .Select(static value => value!.Value));
        }

        if (result.StreamFirstTokenLatency?.TotalMilliseconds is > 0)
        {
            values.Add(result.StreamFirstTokenLatency.Value.TotalMilliseconds);
        }

        return values.Count > 0 ? values.ToArray() : [];
    }

    private static void AddPositiveSample(List<double> values, double? sample)
    {
        if (sample is > 0)
        {
            values.Add(sample.Value);
        }
    }

    private static double[] ExtractOutputThroughputSamples(ProxyThroughputBenchmarkResult? result)
    {
        if (result?.Samples is not { Count: > 0 } samples)
        {
            return result?.MedianOutputTokensPerSecond is { } median ? [median] : [];
        }

        var values = samples
            .Select(static sample => sample.OutputTokensPerSecond ?? sample.EndToEndTokensPerSecond)
            .Where(IsUsableThroughputChartSample)
            .Select(static value => value!.Value)
            .ToArray();

        return values.Length > 0
            ? values
            : IsUsableThroughputChartSample(result?.MedianOutputTokensPerSecond) && result?.MedianOutputTokensPerSecond is { } fallbackMedian
                ? [fallbackMedian]
                : [];
    }

    private static double[] ExtractEndToEndThroughputSamples(
        ProxyThroughputBenchmarkResult? result,
        IReadOnlyList<double> fallbackOutputSamples)
    {
        if (result?.Samples is not { Count: > 0 } samples)
        {
            return fallbackOutputSamples.ToArray();
        }

        var values = samples
            .Select(static sample => sample.EndToEndTokensPerSecond ?? sample.OutputTokensPerSecond)
            .Where(IsUsableThroughputChartSample)
            .Select(static value => value!.Value)
            .ToArray();

        return values.Length > 0 ? values : fallbackOutputSamples.ToArray();
    }

    private static string BuildResponseJson(ProxyDiagnosticsResult result)
    {
        var payload = new
        {
            checkedAt = result.CheckedAt,
            baseUrl = result.BaseUrl,
            requestedModel = result.RequestedModel,
            effectiveModel = result.EffectiveModel,
            verdict = result.Verdict,
            recommendation = result.Recommendation,
            primaryIssue = result.PrimaryIssue,
            summary = result.Summary,
            error = result.Error,
            trace = new
            {
                requestId = result.RequestId,
                traceId = result.TraceId,
                headers = result.ResponseHeadersSummary,
                edge = result.CdnSummary,
            },
            latencies = new
            {
                modelsMs = result.ModelsLatency?.TotalMilliseconds,
                chatMs = result.ChatLatency?.TotalMilliseconds,
                ttftMs = result.StreamFirstTokenLatency?.TotalMilliseconds,
                streamMs = result.StreamDuration?.TotalMilliseconds,
            },
            throughput = result.ThroughputBenchmarkResult,
            longStreaming = result.LongStreamingResult,
            multiModelSpeed = result.MultiModelSpeedResults,
            scenarios = result.ScenarioResults?.Select(scenario => new
            {
                scenario = scenario.Scenario.ToString(),
                scenario.DisplayName,
                scenario.CapabilityStatus,
                scenario.Success,
                scenario.StatusCode,
                latencyMs = scenario.Latency?.TotalMilliseconds,
                firstTokenMs = scenario.FirstTokenLatency?.TotalMilliseconds,
                scenario.OutputTokenCount,
                scenario.OutputTokensPerSecond,
                scenario.Summary,
                scenario.Preview,
                scenario.Error,
                failureKind = scenario.FailureKind?.ToString(),
                scenario.RequestId,
                scenario.TraceId,
            }).ToArray(),
            preview = new
            {
                chat = result.ChatPreview,
                stream = result.StreamPreview,
            },
        };

        return System.Text.Json.JsonSerializer.Serialize(
            payload,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    private static string BuildTraceTimings(ProxyDiagnosticsResult result)
    {
        StringBuilder builder = new();
        builder.AppendLine($"模型列表: {FormatTimeSpan(result.ModelsLatency)}");
        builder.AppendLine($"聊天请求: {FormatTimeSpan(result.ChatLatency)}");
        builder.AppendLine($"TTFT: {FormatTimeSpan(result.StreamFirstTokenLatency)}");
        builder.AppendLine($"流式总时长: {FormatTimeSpan(result.StreamDuration)}");
        builder.AppendLine($"Throughput: {FormatTokensPerSecond(result.ThroughputBenchmarkResult?.MedianOutputTokensPerSecond)}");
        builder.AppendLine($"Request-ID: {result.RequestId ?? "0"}");
        builder.AppendLine($"Trace-ID: {result.TraceId ?? "0"}");
        builder.AppendLine($"追踪性: {result.TraceabilitySummary ?? "0"}");
        builder.Append($"CDN: {result.CdnSummary ?? "0"}");
        return builder.ToString();
    }

    private static string BuildTestLog(ProxyDiagnosticsResult result, string modeName)
    {
        StringBuilder builder = new();
        builder.AppendLine($"[{DateTime.Now:HH:mm:ss}] {TranslateModeName(modeName)}完成");
        builder.AppendLine($"结论: {TranslateVerdict(result.Verdict)}");
        builder.AppendLine($"摘要: {result.Summary}");
        if (result.ScenarioResults is { Count: > 0 })
        {
            foreach (var scenario in result.ScenarioResults)
            {
                builder.AppendLine(
                    $"- {scenario.DisplayName}: {scenario.CapabilityStatus}, {FormatTimeSpan(scenario.Latency)}, {scenario.Summary}");
            }
        }

        if (result.LongStreamingResult is { } longStreaming)
        {
            builder.AppendLine($"长流式: {longStreaming.Summary}");
        }

        if (result.MultiModelSpeedResults is { Count: > 0 })
        {
            foreach (var speed in result.MultiModelSpeedResults)
            {
                builder.AppendLine($"多模型 {speed.Model}: {speed.Summary}");
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static string ResolveContentType(ProxyDiagnosticsResult result)
    {
        var contentTypeLine = (result.ResponseHeadersSummary ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => line.StartsWith("content-type", StringComparison.OrdinalIgnoreCase));
        if (contentTypeLine is null)
        {
            return "application/json";
        }

        var separatorIndex = contentTypeLine.IndexOf(':');
        return separatorIndex >= 0 && separatorIndex + 1 < contentTypeLine.Length
            ? contentTypeLine[(separatorIndex + 1)..].Trim()
            : contentTypeLine;
    }

    private static string BuildCapabilitySummary(ProxyDiagnosticsResult result)
    {
        var scenarios = result.ScenarioResults ?? [];
        if (scenarios.Count == 0)
        {
            return result.ChatRequestSucceeded ? "\u5168\u90E8\u901A\u8FC7" : "\u9700\u590D\u6838";
        }

        var passed = scenarios.Count(static scenario => scenario.Success);
        return $"{passed}/{scenarios.Count} 通过";
    }

    private static string FormatTimeSpan(TimeSpan? value)
        => value.HasValue ? $"{value.Value.TotalMilliseconds:F0} ms" : "0 ms";

    private static string FormatTokensPerSecond(double? value)
        => value.HasValue ? $"{value.Value:F1} tok/s" : "0 tok/s";

    private static string FormatCompactTokensPerSecond(double? value)
    {
        if (!value.HasValue)
        {
            return "0 tok/s";
        }

        return value.Value >= 1_000d
            ? $"{value.Value / 1_000d:F1}K/s"
            : $"{value.Value:F1}/s";
    }

    private static double? AverageOrNull(IReadOnlyList<double> values)
        => values.Count == 0 ? null : values.Average();

    private static bool IsUsableThroughputChartSample(double? value)
        => value is > 0 and <= MaximumDisplayedTokensPerSecond &&
           !double.IsNaN(value.Value) &&
           !double.IsInfinity(value.Value);

    private static string FormatNullableMilliseconds(double? value)
        => value.HasValue ? $"{value.Value:F0} ms" : "0 ms";

    private static string GetModeDisplayName(TestMode mode) => mode switch
    {
        TestMode.Quick => "快速",
        TestMode.Stability => "稳定性",
        TestMode.Deep => "深度",
        TestMode.Concurrency => "并发",
        _ => mode.ToString()
    };

    private static string TranslateModeName(string modeName)
    {
        if (modeName.Contains("Quick", StringComparison.OrdinalIgnoreCase)) return "快速模式";
        if (modeName.Contains("Stability", StringComparison.OrdinalIgnoreCase)) return "稳定性模式";
        if (modeName.Contains("Deep", StringComparison.OrdinalIgnoreCase)) return "深度模式";
        if (modeName.Contains("Concurrency", StringComparison.OrdinalIgnoreCase)) return "并发模式";
        if (modeName.Contains("Multi-model", StringComparison.OrdinalIgnoreCase)) return "多模型测速";
        return modeName;
    }

    private static string TranslateVerdict(string? verdict)
        => verdict is null ? "0"
            : verdict.Equals("Pass", StringComparison.OrdinalIgnoreCase) ? "通过"
            : verdict.Equals("Fail", StringComparison.OrdinalIgnoreCase) ? "失败"
            : verdict;

    private static bool IsCancellationStatus(string? status)
        => !string.IsNullOrWhiteSpace(status) &&
           (status.Contains("cancel", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("Cancel", StringComparison.Ordinal) ||
            status.Contains("取消", StringComparison.Ordinal));

    private static CapabilityState ToCapabilityState(ProxyProbeScenarioResult? scenario)
    {
        if (scenario is null)
        {
            return CapabilityState.Unknown;
        }

        return scenario.Success
            ? CapabilityState.Supported
            : string.Equals(scenario.CapabilityStatus, "\u672A\u914D\u7F6E", StringComparison.Ordinal)
                ? CapabilityState.Unknown
                : CapabilityState.Unsupported;
    }

    private void ApplyNonChatCapabilityStates(ProxyDiagnosticsResult result)
    {
        var scenarios = result.ScenarioResults ?? [];
        ApplyNonChatCapabilityState(8, scenarios.FirstOrDefault(s => s.Scenario == ProxyProbeScenarioKind.Embeddings));
        ApplyNonChatCapabilityState(9, scenarios.FirstOrDefault(s => s.Scenario == ProxyProbeScenarioKind.Images));
        ApplyNonChatCapabilityState(10, scenarios.FirstOrDefault(s => s.Scenario == ProxyProbeScenarioKind.AudioTranscription));
        ApplyNonChatCapabilityState(11, scenarios.FirstOrDefault(s => s.Scenario == ProxyProbeScenarioKind.AudioSpeech));
        ApplyNonChatCapabilityState(12, scenarios.FirstOrDefault(s => s.Scenario == ProxyProbeScenarioKind.Moderation));
    }

    private void ApplyNonChatCapabilityState(int index, ProxyProbeScenarioResult? scenario)
    {
        if (scenario is null)
        {
            return;
        }

        SetCapabilityState(
            index,
            ToCapabilityState(scenario),
            FormatTimeSpan(scenario.Latency),
            scenario.StatusCode?.ToString() ?? scenario.CapabilityStatus);
    }

    private void UpsertDeepScenario(ProxyProbeScenarioResult scenario)
    {
        var item = CreateDeepScenarioResult(scenario, DeepScenarios.Count + 1);

        for (var index = 0; index < DeepScenarios.Count; index++)
        {
            if (string.Equals(DeepScenarios[index].Name, item.Name, StringComparison.OrdinalIgnoreCase))
            {
                DeepScenarios[index] = item;
                return;
            }
        }

        DeepScenarios.Add(item);
    }

    internal static DeepScenarioResult CreateDeepScenarioResult(ProxyProbeScenarioResult scenario, int order = 0)
    {
        var metric = scenario.FirstTokenLatency ?? scenario.Latency ?? scenario.Duration;
        var section = ResolveDeepScenarioSection(scenario.Scenario);
        var metricText = scenario.OutputTokensPerSecond is > 0
            ? FormatTokensPerSecond(scenario.OutputTokensPerSecond)
            : FormatTimeSpan(metric);
        var description = FirstNonEmptyForDisplay(scenario.Summary, scenario.Error, scenario.CapabilityStatus) ?? "";

        return new DeepScenarioResult
        {
            Order = order,
            SectionName = section.Name,
            SectionHint = section.Hint,
            Name = string.IsNullOrWhiteSpace(scenario.DisplayName)
                ? scenario.Scenario.ToString()
                : scenario.DisplayName,
            Description = description,
            Passed = scenario.Success,
            StatusText = scenario.Success ? "通过" : ResolveDeepScenarioStatusText(scenario),
            Latency = FormatTimeSpan(metric),
            StatusCode = scenario.StatusCode,
            MetricValueMs = metric?.TotalMilliseconds,
            MetricText = metricText,
            ReceivedDone = scenario.ReceivedDone,
            DetailText = BuildDeepScenarioDetailText(scenario, section.Name, metricText),
            PreviewText = NormalizeDisplayText(scenario.Preview),
            Trace = scenario.Trace,
        };
    }

    private static (string Name, string Hint) ResolveDeepScenarioSection(ProxyProbeScenarioKind kind)
        => kind switch
        {
            ProxyProbeScenarioKind.Models or
            ProxyProbeScenarioKind.ChatCompletions or
            ProxyProbeScenarioKind.ChatCompletionsStream or
            ProxyProbeScenarioKind.Responses or
            ProxyProbeScenarioKind.AnthropicMessages or
            ProxyProbeScenarioKind.StructuredOutput
                => ("基础协议", "模型列表、对话、流式、Responses、Messages 和结构化输出"),
            ProxyProbeScenarioKind.Embeddings or
            ProxyProbeScenarioKind.Images or
            ProxyProbeScenarioKind.AudioTranscription or
            ProxyProbeScenarioKind.AudioSpeech or
            ProxyProbeScenarioKind.Moderation
                => ("非聊天 API", "向量、图像、音频和审核能力"),
            ProxyProbeScenarioKind.SystemPromptMapping or
            ProxyProbeScenarioKind.FunctionCalling or
            ProxyProbeScenarioKind.ErrorTransparency or
            ProxyProbeScenarioKind.StreamingIntegrity or
            ProxyProbeScenarioKind.OfficialReferenceIntegrity or
            ProxyProbeScenarioKind.MultiModal or
            ProxyProbeScenarioKind.CacheMechanism or
            ProxyProbeScenarioKind.InstructionFollowing or
            ProxyProbeScenarioKind.DataExtraction or
            ProxyProbeScenarioKind.StructuredOutputEdge or
            ProxyProbeScenarioKind.ToolCallDeep or
            ProxyProbeScenarioKind.ReasonMathConsistency or
            ProxyProbeScenarioKind.CodeBlockDiscipline or
            ProxyProbeScenarioKind.CacheIsolation
                => ("深度探针", "协议兼容、语义稳定、缓存和工具调用深测"),
            _ => ("诊断场景", "单站探针场景")
        };

    private static string ResolveDeepScenarioStatusText(ProxyProbeScenarioResult scenario)
    {
        if (scenario.Success)
        {
            return "通过";
        }

        if (scenario.FailureKind is ProxyFailureKind.ConfigurationInvalid)
        {
            return "配置不足";
        }

        return string.IsNullOrWhiteSpace(scenario.CapabilityStatus)
            ? "失败"
            : scenario.CapabilityStatus;
    }

    private static string BuildDeepScenarioDetailText(
        ProxyProbeScenarioResult scenario,
        string sectionName,
        string metricText)
    {
        List<string> lines =
        [
            $"分组：{sectionName}",
            $"结果：{(scenario.Success ? "通过" : ResolveDeepScenarioStatusText(scenario))}",
            $"指标：{metricText}",
            $"HTTP：{(scenario.StatusCode.HasValue ? scenario.StatusCode.Value.ToString() : "--")}",
            $"DONE：{(scenario.ReceivedDone ? "已收到" : "未收到或不适用")}",
            $"分片：{scenario.ChunkCount}",
        ];

        if (!string.IsNullOrWhiteSpace(scenario.CapabilityStatus))
        {
            lines.Add($"状态：{scenario.CapabilityStatus}");
        }

        AddScenarioDetailLine(lines, "摘要", scenario.Summary);
        AddScenarioDetailLine(lines, "预览", scenario.Preview);
        AddScenarioDetailLine(lines, "错误", scenario.Error);

        if (scenario.Trace is not null)
        {
            lines.Add($"Trace：{scenario.Trace.Verdict} / {scenario.Trace.Path}");
        }

        if (!string.IsNullOrWhiteSpace(scenario.RequestId))
        {
            lines.Add($"RequestId：{scenario.RequestId}");
        }

        if (!string.IsNullOrWhiteSpace(scenario.TraceId))
        {
            lines.Add($"TraceId：{scenario.TraceId}");
        }

        return string.Join("\n", lines);
    }

    private static void AddScenarioDetailLine(List<string> lines, string label, string? value)
    {
        var normalized = NormalizeDisplayText(value);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            lines.Add($"{label}：{TrimDisplayText(normalized, 220)}");
        }
    }

    private static string? FirstNonEmptyForDisplay(params string?[] values)
        => values.Select(NormalizeDisplayText).FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));

    private static string NormalizeDisplayText(string? text)
        => string.IsNullOrWhiteSpace(text)
            ? string.Empty
            : text.Replace("\r", " ", StringComparison.Ordinal)
                .Replace("\n", " ", StringComparison.Ordinal)
                .Trim();

    private static string TrimDisplayText(string text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length <= maxLength)
        {
            return text;
        }

        return text[..Math.Max(1, maxLength - 1)] + "…";
    }

    private void ApplyScenarioCapabilityState(ProxyProbeScenarioResult scenario)
    {
        var state = ToCapabilityState(scenario);
        var latency = FormatTimeSpan(scenario.FirstTokenLatency ?? scenario.Latency ?? scenario.Duration);
        var secondary = scenario.OutputTokensPerSecond.HasValue
            ? FormatTokensPerSecond(scenario.OutputTokensPerSecond)
            : scenario.StatusCode?.ToString() ?? scenario.CapabilityStatus;

        switch (scenario.Scenario)
        {
            case ProxyProbeScenarioKind.Models:
                SetCapabilityState(0, state, latency, scenario.OutputTokenCount?.ToString() ?? secondary);
                break;
            case ProxyProbeScenarioKind.ChatCompletions:
            case ProxyProbeScenarioKind.ChatCompletionsStream:
                SetCapabilityState(1, state, latency, secondary);
                ChatSupported = scenario.Success || ChatSupported;
                break;
            case ProxyProbeScenarioKind.Responses:
                SetCapabilityState(2, state, latency, secondary);
                ResponsesSupported = scenario.Success || ResponsesSupported;
                break;
            case ProxyProbeScenarioKind.AnthropicMessages:
                SetCapabilityState(3, state, latency, secondary);
                AnthropicSupported = scenario.Success || AnthropicSupported;
                break;
            case ProxyProbeScenarioKind.StructuredOutput:
            case ProxyProbeScenarioKind.StructuredOutputEdge:
                SetCapabilityState(4, state, latency, secondary);
                break;
            case ProxyProbeScenarioKind.FunctionCalling:
            case ProxyProbeScenarioKind.ToolCallDeep:
                SetCapabilityState(5, state, latency, secondary);
                break;
            case ProxyProbeScenarioKind.ErrorTransparency:
                SetCapabilityState(6, state, latency, secondary);
                break;
            case ProxyProbeScenarioKind.MultiModal:
                SetCapabilityState(7, state, latency, secondary);
                break;
            case ProxyProbeScenarioKind.Embeddings:
                SetCapabilityState(8, state, latency, secondary);
                break;
            case ProxyProbeScenarioKind.Images:
                SetCapabilityState(9, state, latency, secondary);
                break;
            case ProxyProbeScenarioKind.AudioTranscription:
                SetCapabilityState(10, state, latency, secondary);
                break;
            case ProxyProbeScenarioKind.AudioSpeech:
                SetCapabilityState(11, state, latency, secondary);
                break;
            case ProxyProbeScenarioKind.Moderation:
                SetCapabilityState(12, state, latency, secondary);
                break;
        }
    }

    private static string AppendLogLine(string existing, string line)
    {
        if (string.IsNullOrWhiteSpace(existing))
        {
            return line;
        }

        var combined = existing + Environment.NewLine + line;
        var lines = combined.Split(Environment.NewLine, StringSplitOptions.None);
        return lines.Length <= 120
            ? combined
            : string.Join(Environment.NewLine, lines.Skip(lines.Length - 120));
    }

    private int CountEnabledDeepProbes()
        => new[]
        {
            EnableProtocolCompatibilityTest,
            EnableErrorTransparencyTest,
            EnableStreamingIntegrityTest,
            EnableOfficialReferenceIntegrityTest,
            EnableMultiModalTest,
            EnableCacheMechanismTest,
            EnableInstructionFollowingTest,
            EnableDataExtractionTest,
            EnableStructuredOutputEdgeTest,
            EnableToolCallDeepTest,
            EnableReasonMathConsistencyTest,
            EnableCodeBlockDisciplineTest,
            EnableLongStreamingTest,
        }.Count(static enabled => enabled);

    private int CountConfiguredCapabilityModels()
        => new[]
        {
            CapabilityEmbeddingsModel,
            CapabilityImagesModel,
            CapabilityAudioTranscriptionModel,
            CapabilityAudioSpeechModel,
            CapabilityModerationModel,
        }.Count(static value => !string.IsNullOrWhiteSpace(value));

    private bool HasConfiguredCapabilityModels()
        => CountConfiguredCapabilityModels() > 0;

    private int GetLongStreamSegmentCount()
        => Math.Clamp(LongStreamSegments, 24, 240);

    private int GetStabilityRoundCount()
        => Math.Clamp(StabilityRounds, 1, 50);

    private int GetStabilityDelayMilliseconds()
        => Math.Clamp(StabilityDelayMs, 0, 30_000);

    private string[] GetSelectedMultiModelBenchmarkModels()
        => (MultiModelBenchmarkModelsText ?? string.Empty)
            .Split(['\r', '\n', ',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static model => !string.IsNullOrWhiteSpace(model))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static void AddDeepScenarioRow(
        ObservableCollection<DeepScenarioResult> rows,
        string name,
        string description,
        bool passed,
        string latency)
    {
        rows.Add(new DeepScenarioResult
        {
            Order = rows.Count + 1,
            SectionName = "附加指标",
            SectionHint = "吞吐、长流式和多模型测速等非场景补充结果",
            Name = name,
            Description = description,
            Passed = passed,
            StatusText = passed ? "通过" : "失败",
            Latency = latency,
            MetricText = latency,
            DetailText = $"分组：附加指标\n结果：{(passed ? "通过" : "失败")}\n指标：{latency}\n摘要：{description}",
            PreviewText = description,
        });
    }

    private static string BuildLineNumberColumn(string text)
    {
        var lineCount = string.IsNullOrEmpty(text) ? 1 : text.Split('\n').Length;
        return string.Join(Environment.NewLine, Enumerable.Range(1, lineCount));
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F1} MB";
    }

    private static string FormatTokenCount(double tokens)
    {
        if (tokens >= 1_000_000) return $"{tokens / 1_000_000:F1}M";
        if (tokens >= 1_000) return $"{tokens / 1_000:F1}K";
        return $"{tokens:F0}";
    }

}
