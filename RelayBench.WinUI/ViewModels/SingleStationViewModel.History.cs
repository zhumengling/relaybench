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
{
    private void ApplyZeroHistoryState()
    {
        TotalTime = "0s";
        ModelsLatency = "0 ms";
        ChatLatency = "0 ms";
        StreamTtft = "0 ms";
        ChatStatusCode = "0";
        ModelCount = "0";
        QuickRequestSize = "0 tokens";
        QuickResponseSize = "0 tokens";
        QuickSuccessRate = "0.0%";
        SuccessRateDetail = "0/0";
        ErrorRateDisplay = "0%";
        QuickAvgLatency = "0 ms";
        QuickThroughput = "0 tok/s";
        LatencyTooltipTime = "History样本 0";
        LatencyTooltipP50 = "最小: 0 ms";
        LatencyTooltipP95 = "平均: 0 ms";
        LatencyTooltipP99 = "最大: 0 ms";
        StreamingInputLabel = "端到端均值 (0 tok/s)";
        StreamingOutputLabel = "生成均值 (0 tok/s)";
        StabilityP50 = "0 ms";
        StabilityP95 = "0 ms";
        StabilityP99 = "0 ms";
        StabilityHealthScore = "0/100";
        StabilitySuccessRate = "0.0%";
        StabilitySummary = "History样本 0";
        RefreshStabilityTrendRows([], StabilityTotalRounds, isRunning: false);
        DeepPassCount = "0";
        DeepFailCount = "0";
        DeepSummary = "History样本 0";
        ConcurrencyPeakThroughput = "0 tok/s";
        ConcurrencyPeakLevel = "0";
        ConcurrencyStableLimit = "0";
        ConcurrencyPracticalLimit = "0";
        ConcurrencyMaxErrorRate = "0%";
        ConcurrencyRateLimitStart = "0";
        ConcurrencyHighRiskLevel = "0";
        Verdict = "0";
        VerdictReason = "History样本 0";
        CapabilitySummary = "0";
        EntryNodeName = "0";
        ProtocolPreferred = "0";
        ProtocolVersion = "0";
        TestTimestamp = "0";
        CompletionReason = "0";
        ResponseContentType = "0";
        RawResponseJson = "";
        ResponseHeaders = "0";
        TraceTimings = "0";
        TestLog = "0";
        ResetCapabilityMetricsToZero();
        BuildQuickLatencyChart([0d]);
        BuildHistoryTtftChart([0d]);
        BuildThroughputChartFromSamples([0d]);
        BuildStreamingTokenChart([0d], [0d]);
    }

    private void ResetCapabilityMetricsToZero()
    {
        for (var index = 0; index < Capabilities.Count; index++)
        {
            var current = Capabilities[index];
            Capabilities[index] = current with
            {
                State = CapabilityState.Unknown,
                PrimaryMetricValue = current.PrimaryMetricLabel.Contains("耗时", StringComparison.Ordinal) ||
                                     current.PrimaryMetricLabel.Contains("TTFT", StringComparison.Ordinal) ||
                                     current.PrimaryMetricLabel.Contains("延迟", StringComparison.Ordinal)
                    ? "0 ms"
                    : "0",
                SecondaryMetricValue = current.SecondaryMetricLabel.Contains("模型数", StringComparison.Ordinal) ||
                                       current.SecondaryMetricLabel.Contains("维度", StringComparison.Ordinal)
                    ? "0"
                    : "0"
            };
        }
    }

    private void LoadHistoricalSingleStationState()
    {
        try
        {
            var latestSummary = _historyRepository
                .QueryAsync(new HistoryQuery(TestType: "单站测试", Limit: 1))
                .GetAwaiter()
                .GetResult()
                .FirstOrDefault();
            if (latestSummary is null)
            {
                StatusText = "暂无History单站测试数据，当前显示 0";
                return;
            }

            var latest = _historyRepository.GetAsync(latestSummary.RunId).GetAwaiter().GetResult();
            if (latest is null)
            {
                StatusText = "暂无History单站测试数据，当前显示 0";
                return;
            }

            ApplySingleStationHistory(latest);
            StatusText = $"已加载History单站测试：{latest.CreatedAtUtc.ToLocalTime():MM-dd HH:mm}";
        }
        catch (Exception ex)
        {
            ApplyZeroHistoryState();
            StatusText = $"History单站数据读取失败，当前显示 0: {ex.Message}";
        }
    }

    private void ApplySingleStationHistory(HistoryReport report)
    {
        RawResponseJson = string.IsNullOrWhiteSpace(report.PayloadJson) ? "{}" : report.PayloadJson;
        if (string.IsNullOrWhiteSpace(BaseUrl))
        {
            BaseUrl = report.Endpoint;
        }

        TotalTime = FormatDurationFromMilliseconds(report.DurationMs ?? 0);
        TestTimestamp = report.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        EntryNodeName = ExtractHostFromUrl(report.Endpoint);
        Verdict = report.Score >= 60 ? "Pass" : report.Score.HasValue ? "Fail" : "0";
        VerdictReason = report.Summary;

        if (!HistoryPayloadReader.TryParse(report.PayloadJson, out var document))
        {
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            var requestedModel = HistoryPayloadReader.FirstString(root, ["requestedModel"], ["RequestedModel"]);
            var effectiveModel = HistoryPayloadReader.FirstString(root, ["effectiveModel"], ["EffectiveModel"]);
            if (string.IsNullOrWhiteSpace(Model))
            {
                Model = effectiveModel ?? requestedModel ?? Model;
            }

            ModelsLatency = FormatMs(HistoryPayloadReader.ReadDouble(root, "latencies", "modelsMs"));
            ChatLatency = FormatMs(HistoryPayloadReader.ReadDouble(root, "latencies", "chatMs"));
            StreamTtft = FormatMs(HistoryPayloadReader.ReadDouble(root, "latencies", "ttftMs"));
            QuickAvgLatency = ChatLatency;
            QuickThroughput = FormatTokensPerSecond(ReadHistoryThroughput(root));
            QuickResponseSize = FormatTokenCount(ReadHistoryOutputTokens(root));
            QuickSuccessRate = $"{ReadHistoryPassRate(root):F1}%";
            SuccessRateDetail = BuildHistorySuccessDetail(root);
            ErrorRateDisplay = $"{100 - ReadHistoryPassRate(root):F1}%";
            ChatStatusCode = ReadHistoryStatusCode(root) ?? "0";
            ModelCount = "0";
            ProtocolPreferred = ResolveHistoryProtocol(root);
            ProtocolVersion = effectiveModel ?? requestedModel ?? "0";
            ResponseHeaders = HistoryPayloadReader.ReadString(root, "trace", "headers") ?? "0";
            TraceTimings = BuildHistoryTraceTimings(root);
            TestLog = BuildHistoryTestLog(root, report);
            CompletionReason = string.Equals(Verdict, "Pass", StringComparison.OrdinalIgnoreCase) ? "stop" : "0";
            ResponseContentType = ResolveHistoryContentType(root);
            ApplyHistoryCapabilityFlags(root);
            BuildQuickChartsFromHistory(root);
        }

        HasQuickResults = true;
    }

    private void BuildQuickChartsFromHistory(JsonElement root)
    {
        var latencyValues = new List<double>();
        foreach (var value in new[]
        {
            HistoryPayloadReader.ReadDouble(root, "latencies", "modelsMs"),
            HistoryPayloadReader.ReadDouble(root, "latencies", "chatMs"),
            HistoryPayloadReader.ReadDouble(root, "latencies", "streamMs")
        })
        {
            if (value is > 0)
            {
                latencyValues.Add(value.Value);
            }
        }

        foreach (var scenario in HistoryPayloadReader.ReadArray(root, "scenarios"))
        {
            var latency = HistoryPayloadReader.ReadDouble(scenario, "latencyMs");
            if (latency is > 0)
            {
                latencyValues.Add(latency.Value);
            }
        }

        var latencySamples = latencyValues.Count > 0 ? latencyValues.ToArray() : [0d];
        BuildQuickLatencyChart(latencySamples);
        LatencyTooltipTime = $"History {TestTimestamp}";
        LatencyTooltipP50 = $"最小: {latencySamples.Min():N0} ms";
        LatencyTooltipP95 = $"平均: {latencySamples.Average():N0} ms";
        LatencyTooltipP99 = $"最大: {latencySamples.Max():N0} ms";

        var ttftValues = HistoryPayloadReader.ReadArray(root, "scenarios")
            .Select(static scenario => HistoryPayloadReader.ReadDouble(scenario, "firstTokenMs"))
            .Append(HistoryPayloadReader.ReadDouble(root, "latencies", "ttftMs"))
            .Where(static value => value is > 0)
            .Select(static value => value!.Value)
            .ToArray();
        BuildHistoryTtftChart(ttftValues.Length > 0 ? ttftValues : [0d]);

        var outputRates = ReadHistoryThroughputSamples(root, output: true);
        var endToEndRates = ReadHistoryThroughputSamples(root, output: false);
        BuildThroughputChartFromSamples(outputRates.Count > 0 ? outputRates : [ReadHistoryThroughput(root) ?? 0d]);
        BuildStreamingTokenChart(
            endToEndRates.Count > 0 ? endToEndRates : outputRates.Count > 0 ? outputRates : [0d],
            outputRates.Count > 0 ? outputRates : [ReadHistoryThroughput(root) ?? 0d]);
    }

    private void BuildHistoryTtftChart(IReadOnlyList<double> values)
    {
        var theme = GetChartTheme();
        var (ttftSeries, _) = TtftDistributionChartBuilder.Build(values, theme);
        QuickTtftChartSeries = ttftSeries;
        QuickTtftChartYAxes = TtftDistributionChartBuilder.BuildYAxes(theme);
        QuickTtftChartXAxes = TtftDistributionChartBuilder.BuildXAxes(theme);
    }

    private static List<double> ReadHistoryThroughputSamples(JsonElement root, bool output)
    {
        var samples = HistoryPayloadReader.ReadArray(root, "throughput", "Samples");
        if (samples.Count == 0)
        {
            samples = HistoryPayloadReader.ReadArray(root, "throughput", "samples");
        }

        return samples
            .Select(sample => HistoryPayloadReader.FirstDouble(
                sample,
                output ? ["OutputTokensPerSecond"] : ["EndToEndTokensPerSecond"],
                output ? ["outputTokensPerSecond"] : ["endToEndTokensPerSecond"]))
            .Where(static value => value is > 0)
            .Select(static value => value!.Value)
            .ToList();
    }

    private static double? ReadHistoryThroughput(JsonElement root)
        => HistoryPayloadReader.FirstDouble(
            root,
            ["throughput", "MedianOutputTokensPerSecond"],
            ["throughput", "medianOutputTokensPerSecond"],
            ["throughput", "AverageOutputTokensPerSecond"],
            ["throughput", "averageOutputTokensPerSecond"]);

    private static int? ReadHistoryOutputTokens(JsonElement root)
    {
        var scenarios = HistoryPayloadReader.ReadArray(root, "scenarios");
        return scenarios
            .Select(static scenario => HistoryPayloadReader.ReadInt(scenario, "OutputTokenCount"))
            .Where(static value => value.HasValue)
            .Select(static value => value!.Value)
            .DefaultIfEmpty(0)
            .Sum();
    }

    private static double ReadHistoryPassRate(JsonElement root)
    {
        var scenarios = HistoryPayloadReader.ReadArray(root, "scenarios");
        if (scenarios.Count == 0)
        {
            var verdict = HistoryPayloadReader.ReadString(root, "verdict");
            return string.Equals(verdict, "Pass", StringComparison.OrdinalIgnoreCase) ? 100 : 0;
        }

        var passed = scenarios.Count(static scenario => HistoryPayloadReader.ReadBool(scenario, "Success") == true);
        return passed * 100.0 / scenarios.Count;
    }

    private static string BuildHistorySuccessDetail(JsonElement root)
    {
        var scenarios = HistoryPayloadReader.ReadArray(root, "scenarios");
        if (scenarios.Count == 0)
        {
            return "0/0";
        }

        var passed = scenarios.Count(static scenario => HistoryPayloadReader.ReadBool(scenario, "Success") == true);
        return $"{passed}/{scenarios.Count}";
    }

    private static string? ReadHistoryStatusCode(JsonElement root)
        => HistoryPayloadReader.ReadArray(root, "scenarios")
            .Select(static scenario => HistoryPayloadReader.FirstString(scenario, ["StatusCode"], ["statusCode"]))
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));

    private static string ResolveHistoryProtocol(JsonElement root)
    {
        var scenarios = HistoryPayloadReader.ReadArray(root, "scenarios");
        if (scenarios.Any(static scenario =>
                string.Equals(HistoryPayloadReader.ReadString(scenario, "scenario"), ProxyProbeScenarioKind.Responses.ToString(), StringComparison.Ordinal) &&
                HistoryPayloadReader.ReadBool(scenario, "Success") == true))
        {
            return "Responses API";
        }

        if (scenarios.Any(static scenario =>
                string.Equals(HistoryPayloadReader.ReadString(scenario, "scenario"), ProxyProbeScenarioKind.AnthropicMessages.ToString(), StringComparison.Ordinal) &&
                HistoryPayloadReader.ReadBool(scenario, "Success") == true))
        {
            return "Anthropic";
        }

        return scenarios.Any(static scenario => HistoryPayloadReader.ReadBool(scenario, "Success") == true) ? "Chat" : "0";
    }

    private static string BuildHistoryTraceTimings(JsonElement root)
    {
        StringBuilder builder = new();
        builder.AppendLine($"模型列表: {FormatMs(HistoryPayloadReader.ReadDouble(root, "latencies", "modelsMs"))}");
        builder.AppendLine($"聊天请求: {FormatMs(HistoryPayloadReader.ReadDouble(root, "latencies", "chatMs"))}");
        builder.AppendLine($"TTFT: {FormatMs(HistoryPayloadReader.ReadDouble(root, "latencies", "ttftMs"))}");
        builder.AppendLine($"流式总时长: {FormatMs(HistoryPayloadReader.ReadDouble(root, "latencies", "streamMs"))}");
        builder.Append($"吞吐: {FormatTokensPerSecond(ReadHistoryThroughput(root))}");
        return builder.ToString();
    }

    private static string BuildHistoryTestLog(JsonElement root, HistoryReport report)
    {
        StringBuilder builder = new();
        builder.AppendLine($"[{report.CreatedAtUtc.ToLocalTime():HH:mm:ss}] 历史单站测试");
        builder.AppendLine($"摘要: {report.Summary}");
        foreach (var scenario in HistoryPayloadReader.ReadArray(root, "scenarios"))
        {
            builder.AppendLine($"- {HistoryPayloadReader.ReadString(scenario, "DisplayName") ?? HistoryPayloadReader.ReadString(scenario, "displayName") ?? "场景"}: {HistoryPayloadReader.ReadString(scenario, "CapabilityStatus") ?? HistoryPayloadReader.ReadString(scenario, "capabilityStatus") ?? "0"}");
        }

        return builder.ToString().TrimEnd();
    }

    private static string ResolveHistoryContentType(JsonElement root)
    {
        var headers = HistoryPayloadReader.ReadString(root, "trace", "headers") ?? string.Empty;
        return headers.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase)
            ? "text/event-stream"
            : headers.Contains("application/json", StringComparison.OrdinalIgnoreCase)
                ? "application/json"
                : "0";
    }

    private void ApplyHistoryCapabilityFlags(JsonElement root)
    {
        var scenarios = HistoryPayloadReader.ReadArray(root, "scenarios");
        ChatSupported = scenarios.Any(static scenario =>
            string.Equals(HistoryPayloadReader.ReadString(scenario, "scenario"), ProxyProbeScenarioKind.ChatCompletions.ToString(), StringComparison.Ordinal) &&
            HistoryPayloadReader.ReadBool(scenario, "Success") == true);
        ResponsesSupported = scenarios.Any(static scenario =>
            string.Equals(HistoryPayloadReader.ReadString(scenario, "scenario"), ProxyProbeScenarioKind.Responses.ToString(), StringComparison.Ordinal) &&
            HistoryPayloadReader.ReadBool(scenario, "Success") == true);
        AnthropicSupported = scenarios.Any(static scenario =>
            string.Equals(HistoryPayloadReader.ReadString(scenario, "scenario"), ProxyProbeScenarioKind.AnthropicMessages.ToString(), StringComparison.Ordinal) &&
            HistoryPayloadReader.ReadBool(scenario, "Success") == true);
        CapabilitySummary = BuildHistorySuccessDetail(root) + " 通过";
    }

    private static string FormatMs(double? value)
        => value.HasValue ? $"{Math.Max(0, value.Value):F0} ms" : "0 ms";

    private static string FormatTokenCount(int? value)
        => value.HasValue && value.Value > 0 ? $"{value.Value:N0} tokens" : "0 tokens";

    private static string FormatDurationFromMilliseconds(int milliseconds)
    {
        var duration = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return duration.TotalSeconds >= 1 ? $"{duration.TotalSeconds:F2}s" : "0s";
    }

    partial void OnSelectedTestModeChanged(TestMode value)
    {
        OnPropertyChanged(nameof(IsQuickMode));
        OnPropertyChanged(nameof(IsStabilityMode));
        OnPropertyChanged(nameof(IsDeepMode));
        OnPropertyChanged(nameof(IsConcurrencyMode));
        OnPropertyChanged(nameof(IsQuickModeVisible));
        OnPropertyChanged(nameof(IsStabilityModeVisible));
        OnPropertyChanged(nameof(IsDeepModeVisible));
        OnPropertyChanged(nameof(IsConcurrencyModeVisible));
        OnPropertyChanged(nameof(IsTestOptionsVisible));
        OnPropertyChanged(nameof(IsSideTestOptionsVisible));
        OnPropertyChanged(nameof(SelectedTestModeIndex));
        UpdateKpiLabels(value);
    }

    partial void OnProtocolIndexChanged(int value)
    {
        OnPropertyChanged(nameof(ProtocolPrefix));
    }

    partial void OnVerdictChanged(string value)
    {
        OnPropertyChanged(nameof(StatusBannerGlyph));
        OnPropertyChanged(nameof(VerdictDisplay));
        OnPropertyChanged(nameof(VerdictPassVisibility));
        OnPropertyChanged(nameof(VerdictFailVisibility));
        OnPropertyChanged(nameof(VerdictNeutralVisibility));
        OnPropertyChanged(nameof(VerdictGlyph));
    }

    partial void OnEntryNodeNameChanged(string value)
    {
        OnPropertyChanged(nameof(EntryNodeDisplay));
    }

    partial void OnChatStatusCodeChanged(string value)
    {
        OnPropertyChanged(nameof(StatusBadgeText));
        OnPropertyChanged(nameof(StatusBadgeSuccessVisibility));
        OnPropertyChanged(nameof(StatusBadgeErrorVisibility));
    }

    partial void OnIsRawResponseExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(ResponseViewerMaxHeight));
        OnPropertyChanged(nameof(RawResponseToggleGlyph));
        OnPropertyChanged(nameof(RawResponseToggleTooltip));
    }

    partial void OnRawResponseJsonChanged(string value)
    {
        RawResponseLineNumbers = BuildLineNumberColumn(value);
    }

    partial void OnEnableProtocolCompatibilityTestChanged(bool value) => NotifyAdvancedExecutionSummaryChanged();
    partial void OnEnableErrorTransparencyTestChanged(bool value) => NotifyAdvancedExecutionSummaryChanged();
    partial void OnEnableStreamingIntegrityTestChanged(bool value) => NotifyAdvancedExecutionSummaryChanged();
    partial void OnEnableMultiModalTestChanged(bool value) => NotifyAdvancedExecutionSummaryChanged();
    partial void OnEnableCacheMechanismTestChanged(bool value) => NotifyAdvancedExecutionSummaryChanged();
    partial void OnEnableInstructionFollowingTestChanged(bool value) => NotifyAdvancedExecutionSummaryChanged();
    partial void OnEnableDataExtractionTestChanged(bool value) => NotifyAdvancedExecutionSummaryChanged();
    partial void OnEnableStructuredOutputEdgeTestChanged(bool value) => NotifyAdvancedExecutionSummaryChanged();
    partial void OnEnableToolCallDeepTestChanged(bool value) => NotifyAdvancedExecutionSummaryChanged();
    partial void OnEnableReasonMathConsistencyTestChanged(bool value) => NotifyAdvancedExecutionSummaryChanged();
    partial void OnEnableCodeBlockDisciplineTestChanged(bool value) => NotifyAdvancedExecutionSummaryChanged();
    partial void OnEnableCacheIsolationTestChanged(bool value) => NotifyAdvancedExecutionSummaryChanged();
    partial void OnEnableOfficialReferenceIntegrityTestChanged(bool value) => NotifyAdvancedExecutionSummaryChanged();
    partial void OnCapabilityEmbeddingsModelChanged(string value) => NotifyAdvancedExecutionSummaryChanged();
    partial void OnCapabilityImagesModelChanged(string value) => NotifyAdvancedExecutionSummaryChanged();
    partial void OnCapabilityAudioTranscriptionModelChanged(string value) => NotifyAdvancedExecutionSummaryChanged();
    partial void OnCapabilityAudioSpeechModelChanged(string value) => NotifyAdvancedExecutionSummaryChanged();
    partial void OnCapabilityModerationModelChanged(string value) => NotifyAdvancedExecutionSummaryChanged();
    partial void OnMultiModelBenchmarkModelsTextChanged(string value)
    {
        NotifyAdvancedExecutionSummaryChanged();
        OnPropertyChanged(nameof(MultiModelBenchmarkModelsDisplay));
    }

}
