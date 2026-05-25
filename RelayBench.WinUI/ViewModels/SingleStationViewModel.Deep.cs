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
{    // ========== Deep Mode ==========
    private async Task RunDeepModeAsync(CancellationToken ct)
    {
        StatusText = "正在运行深度协议测试...";
        HasDeepResults = false;
        DeepScenarios.Clear();
        MultiModelSpeedResults.Clear();
        ResetThroughputSamplingBuffers();

        var settings = BuildSettings();
        ProxyDiagnosticsResult? baseline = null;

        try
        {
            var liveProgress = new Progress<ProxyDiagnosticsLiveProgress>(ApplyProxyLiveProgressSafe);

            // First run baseline
            baseline = await _diagnosticsService.RunAsync(settings, liveProgress, ct);
            ApplyDeepResult(baseline);
            ApplyCommonDiagnosticsDetails(baseline, "Deep baseline");
            UpdateKpiLabels(SelectedTestMode);

            ct.ThrowIfCancellationRequested();

            StatusText = "正在运行独立吞吐测试...";
            var throughputSettings = string.IsNullOrWhiteSpace(baseline.EffectiveModel)
                ? settings
                : settings with { Model = baseline.EffectiveModel.Trim() };
            var throughputBenchmark = await _diagnosticsService.RunThroughputBenchmarkAsync(
                throughputSettings,
                baselineResult: baseline,
                liveProgress: new Progress<ProxyThroughputBenchmarkLiveProgress>(ApplyThroughputLiveProgressSafe),
                cancellationToken: ct);
            var result = baseline with { ThroughputBenchmarkResult = throughputBenchmark };
            ApplyDeepResult(result);
            ApplyCommonDiagnosticsDetails(result, "Deep throughput");
            UpdateKpiLabels(SelectedTestMode);

            // Run supplemental scenarios for deep protocol testing
            StatusText = "正在运行高级协议探针...";
            var deepResult = await _diagnosticsService.RunSupplementalScenariosAsync(
                settings,
                result,
                includeProtocolCompatibility: EnableProtocolCompatibilityTest,
                includeErrorTransparency: EnableErrorTransparencyTest,
                includeStreamingIntegrity: EnableStreamingIntegrityTest,
                includeOfficialReferenceIntegrity: EnableOfficialReferenceIntegrityTest,
                officialReferenceBaseUrl: OfficialReferenceBaseUrl,
                officialReferenceApiKey: OfficialReferenceApiKey,
                officialReferenceModel: OfficialReferenceModel,
                includeMultiModal: EnableMultiModalTest,
                includeCacheMechanism: EnableCacheMechanismTest,
                includeCacheIsolation: false,
                cacheIsolationAlternateApiKey: null,
                includeInstructionFollowing: EnableInstructionFollowingTest,
                includeDataExtraction: EnableDataExtractionTest,
                includeStructuredOutputEdge: EnableStructuredOutputEdgeTest,
                includeToolCallDeep: EnableToolCallDeepTest,
                includeReasonMathConsistency: EnableReasonMathConsistencyTest,
                includeCodeBlockDiscipline: EnableCodeBlockDisciplineTest,
                progress: liveProgress,
                cancellationToken: ct);
            result = deepResult;
            ApplyDeepResult(result);
            ApplyCommonDiagnosticsDetails(result, "Deep probes");
            UpdateKpiLabels(SelectedTestMode);

            if (HasConfiguredCapabilityModels())
            {
        StatusText = "正在探测非聊天 API 能力...";
                result = await _diagnosticsService.RunNonChatCapabilityMatrixAsync(
                    settings,
                    result,
                    progress: liveProgress,
                    cancellationToken: ct);
                ApplyDeepResult(result);
                ApplyCommonDiagnosticsDetails(result, "Non-chat API");
                UpdateKpiLabels(SelectedTestMode);
            }

            if (EnableLongStreamingTest)
            {
                StatusText = $"正在运行长流式测试（{GetLongStreamSegmentCount()} 段）...";
                var longStreamingSettings = string.IsNullOrWhiteSpace(result.EffectiveModel)
                    ? settings
                    : settings with { Model = result.EffectiveModel.Trim() };
                var longStreamingResult = await _diagnosticsService.RunLongStreamingTestAsync(
                    longStreamingSettings,
                    GetLongStreamSegmentCount(),
                    ct);
                result = result with { LongStreamingResult = longStreamingResult };
                ApplyDeepResult(result);
                ApplyCommonDiagnosticsDetails(result, "Long streaming");
                UpdateKpiLabels(SelectedTestMode);
            }

            var multiModelModels = GetSelectedMultiModelBenchmarkModels();
            if (multiModelModels.Length > 0)
            {
                StatusText = $"正在运行多模型测速（0/{multiModelModels.Length}）...";
                List<ProxyMultiModelSpeedTestResult> speedResults = [];
                for (var i = 0; i < multiModelModels.Length; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    StatusText = $"正在运行多模型测速（{i + 1}/{multiModelModels.Length}）：{multiModelModels[i]}";
                    var current = await _diagnosticsService.RunMultiModelSpeedTestAsync(
                        settings,
                        [multiModelModels[i]],
                        ct);
                    speedResults.AddRange(current);
                    result = result with { MultiModelSpeedResults = speedResults };
                    ApplyDeepResult(result);
                    ApplyCommonDiagnosticsDetails(result, "Multi-model speed");
                    UpdateKpiLabels(SelectedTestMode);
                }

                result = result with { MultiModelSpeedResults = speedResults };
            }

            ApplyDeepResult(result);
            ApplyCommonDiagnosticsDetails(result, "Deep");
        }
        catch (OperationCanceledException)
        {
            // Display partial results from whatever completed before cancellation
            if (baseline != null)
            {
                ApplyPartialDeepResult(baseline);
                StatusText = $"深度测试已取消，已显示 {DeepScenarios.Count} 个场景结果";
            }
            else
            {
                StatusText = "深度测试在基准完成前已取消";
            }
            throw; // Re-throw so StartTestAsync knows it was cancelled
        }
    }

    /// <summary>
    /// Applies partial deep mode results from the baseline when supplemental scenarios are cancelled.
    /// </summary>
    private void ApplyPartialDeepResult(ProxyDiagnosticsResult baseline)
    {
        DeepScenarios.Clear();

        // Show whatever scenario results exist in the baseline
        var scenarios = baseline.ScenarioResults ?? [];
        int passCount = 0, failCount = 0;

        foreach (var scenario in scenarios)
        {
            var item = CreateDeepScenarioResult(scenario, DeepScenarios.Count + 1);
            DeepScenarios.Add(item);

            if (scenario.Success) passCount++;
            else failCount++;
        }

        // Also add the baseline chat result as a scenario
        var baselineItem = new DeepScenarioResult
        {
            Order = DeepScenarios.Count + 1,
            SectionName = "基础协议",
            SectionHint = "基准聊天补全",
            Name = "基准聊天",
            Description = "Basic chat completion test",
            Passed = baseline.ChatRequestSucceeded,
            StatusText = baseline.ChatRequestSucceeded ? "通过" : "失败",
            Latency = baseline.ChatLatency.HasValue
                ? $"{baseline.ChatLatency.Value.TotalMilliseconds:F0} ms" : "0 ms",
            MetricValueMs = baseline.ChatLatency?.TotalMilliseconds,
            MetricText = baseline.ChatLatency.HasValue ? $"{baseline.ChatLatency.Value.TotalMilliseconds:F0} ms" : "0 ms",
            PreviewText = baseline.ChatPreview ?? baseline.Summary ?? "",
            DetailText = $"分组：基础协议\n结果：{(baseline.ChatRequestSucceeded ? "通过" : "失败")}\n指标：{FormatTimeSpan(baseline.ChatLatency)}\nHTTP：{(baseline.ChatStatusCode.HasValue ? baseline.ChatStatusCode.Value.ToString() : "--")}\n摘要：{baseline.Summary}",
        };
        DeepScenarios.Add(baselineItem);
        if (baseline.ChatRequestSucceeded) passCount++;
        else failCount++;

        DeepPassCount = $"{passCount}";
        DeepFailCount = $"{failCount}";
        DeepSummary = $"已取消：完成 {passCount + failCount} 个场景";
        Verdict = passCount > failCount ? "Pass" : "Fail";
        HasDeepResults = true;
    }

    private void ApplyDeepResult(ProxyDiagnosticsResult result)
    {
        DeepScenarios.Clear();
        MultiModelSpeedResults.Clear();

        var scenarios = result.ScenarioResults ?? [];
        int passCount = 0, failCount = 0;

        foreach (var scenario in scenarios)
        {
            var item = CreateDeepScenarioResult(scenario, DeepScenarios.Count + 1);
            DeepScenarios.Add(item);

            if (scenario.Success) passCount++;
            else failCount++;
        }

        if (result.ThroughputBenchmarkResult is { } throughput)
        {
            AddDeepScenarioRow(
                DeepScenarios,
                "独立吞吐测试",
                throughput.Summary,
                throughput.SuccessfulSampleCount > 0,
                FormatTokensPerSecond(throughput.MedianOutputTokensPerSecond));
            if (throughput.SuccessfulSampleCount > 0) passCount++;
            else failCount++;
        }

        if (result.LongStreamingResult is { } longStreaming)
        {
            AddDeepScenarioRow(
                DeepScenarios,
                "长流式测试",
                longStreaming.Summary,
                longStreaming.Success,
                FormatTimeSpan(longStreaming.FirstTokenLatency));
            if (longStreaming.Success) passCount++;
            else failCount++;
        }

        if (result.MultiModelSpeedResults is { Count: > 0 } speedResults)
        {
            foreach (var speed in speedResults)
            {
                MultiModelSpeedResults.Add(new MultiModelSpeedResult
                {
                    Model = speed.Model,
                    Success = speed.Success,
                    ThroughputText = FormatTokensPerSecond(speed.OutputTokensPerSecond),
                    Summary = speed.Summary,
                });

                AddDeepScenarioRow(
                    DeepScenarios,
                    $"模型测速：{speed.Model}",
                    speed.Summary,
                    speed.Success,
                    FormatTokensPerSecond(speed.OutputTokensPerSecond));
                if (speed.Success) passCount++;
                else failCount++;
            }
        }

        DeepPassCount = $"{passCount}";
        DeepFailCount = $"{failCount}";
        DeepSummary = result.Summary;
        Verdict = result.Verdict ?? (passCount > failCount ? "Pass" : "Fail");
        SetCapabilityState(4, HasScenario(result, ProxyProbeScenarioKind.StructuredOutputEdge) ? CapabilityState.Supported : CapabilityState.Unknown);
        SetCapabilityState(5, HasScenario(result, ProxyProbeScenarioKind.ToolCallDeep) || HasScenario(result, ProxyProbeScenarioKind.FunctionCalling) ? CapabilityState.Supported : CapabilityState.Unknown);
        SetCapabilityState(6, HasScenario(result, ProxyProbeScenarioKind.ErrorTransparency) ? CapabilityState.Supported : CapabilityState.Unknown);
        SetCapabilityState(7, HasScenario(result, ProxyProbeScenarioKind.MultiModal) ? CapabilityState.Supported : CapabilityState.Unknown);
        ApplyNonChatCapabilityStates(result);
        HasDeepResults = true;

        StatusText = $"深度测试完成：{passCount} 通过，{failCount} 失败";
    }

}
