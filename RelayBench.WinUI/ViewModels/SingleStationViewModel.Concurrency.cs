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
{    // ========== Concurrency Mode ==========
    private async Task RunConcurrencyModeAsync(CancellationToken ct)
    {
        StatusText = "正在运行并发测试...";
        HasConcurrencyResults = false;
        ConcurrencyLevels.Clear();

        var settings = BuildSettings();
        int[] levels = [1, 2, 4, 8, 16];
        List<ProxyConcurrencyPressureStageResult> completedStages = [];
        var preferredWireApi = await ResolveCachedConversationWireApiAsync(settings, ct);

        try
        {
            var stageProgress = new Progress<ProxyConcurrencyPressureStageResult>(stage =>
            {
                try
                {
                    completedStages.Add(stage);
                    ApplyPartialConcurrencyResults(settings, completedStages, levels.Length);
                    StatusText = $"并发：完成档位 {stage.Concurrency}（{completedStages.Count}/{levels.Length}）";
                }
                catch (Exception ex)
                {
                    RelayBench.Services.Infrastructure.AppDiagnosticLog.Write("SingleStation.ConcurrencyProgress", ex);
                    StatusText = $"并发档位 {stage.Concurrency} 已完成，但 UI 刷新失败，已记录日志";
                }
            });

            var result = await _diagnosticsService.RunConcurrencyPressureAsync(
                settings,
                levels,
                stageProgress,
                ct,
                preferredWireApi);

            ApplyConcurrencyResults(result);
        }
        catch (OperationCanceledException) when (completedStages.Count > 0)
        {
            // Display partial results from concurrency levels completed before cancellation
            var partialResult = new ProxyConcurrencyPressureResult(
                DateTimeOffset.Now,
                settings.BaseUrl,
                settings.Model,
                completedStages,
                StableConcurrencyLimit: null,
                RateLimitStartConcurrency: null,
                HighRiskConcurrency: null,
                Summary: $"已取消：完成 {completedStages.Count}/{levels.Length} 个档位",
                Error: null,
                PracticalConcurrencyLimit: null);
            ApplyConcurrencyResults(partialResult);
            StatusText = $"并发测试已取消，已显示 {completedStages.Count}/{levels.Length} 个档位结果";
            throw; // Re-throw so StartTestAsync knows it was cancelled
        }
    }

    private void ApplyConcurrencyResults(ProxyConcurrencyPressureResult result)
    {
        if (result.Stages.Count == 0) return;

        ConcurrencyLevels.Clear();
        var orderedStages = result.Stages
            .OrderBy(static stage => stage.Concurrency)
            .ToArray();
        var maxThroughput = orderedStages
            .Select(static stage => stage.AverageTokensPerSecond ?? 0d)
            .DefaultIfEmpty(0d)
            .Max();
        var maxP50 = orderedStages
            .Select(static stage => stage.P50ChatLatencyMs ?? 0d)
            .DefaultIfEmpty(0d)
            .Max();
        var maxP95Ttft = orderedStages
            .Select(static stage => stage.P95TtftMs ?? 0d)
            .DefaultIfEmpty(0d)
            .Max();

        foreach (var stage in orderedStages)
        {
            ConcurrencyLevels.Add(BuildConcurrencyLevelResult(
                stage,
                result.StableConcurrencyLimit,
                result.RateLimitStartConcurrency,
                result.HighRiskConcurrency,
                maxThroughput,
                maxP50,
                maxP95Ttft));
        }

        var peak = ConcurrencyLevels.OrderByDescending(r => r.Throughput).First();
        ConcurrencyPeakThroughput = peak.ThroughputText;
        ConcurrencyPeakLevel = peak.LevelText;
        ConcurrencyMaxErrorRate = $"{ConcurrencyLevels.Max(r => r.ErrorRate):F1}%";
        ConcurrencyStableLimit = FormatConcurrencyLevel(result.StableConcurrencyLimit);
        ConcurrencyPracticalLimit = FormatConcurrencyLevel(result.PracticalConcurrencyLimit);
        ConcurrencyRateLimitStart = FormatConcurrencyLevel(result.RateLimitStartConcurrency);
        ConcurrencyHighRiskLevel = FormatConcurrencyLevel(result.HighRiskConcurrency);
        ConcurrencySummary = result.Summary;
        Verdict = string.IsNullOrWhiteSpace(result.Error) ? "Pass" : "Fail";
        VerdictReason = result.Error ?? result.Summary;

        HasConcurrencyResults = true;
        BuildConcurrencyChart(ConcurrencyLevels.ToList());

        StatusText = $"并发测试完成：峰值 {peak.ThroughputText}，档位 {peak.Level}";
    }

    private void ApplyPartialConcurrencyResults(
        ProxyEndpointSettings settings,
        List<ProxyConcurrencyPressureStageResult> completedStages,
        int totalLevelCount)
    {
        if (completedStages.Count == 0)
        {
            return;
        }

        var result = new ProxyConcurrencyPressureResult(
            DateTimeOffset.Now,
            settings.BaseUrl,
            settings.Model,
            completedStages.ToArray(),
            StableConcurrencyLimit: null,
            RateLimitStartConcurrency: null,
            HighRiskConcurrency: null,
            Summary: $"已完成 {completedStages.Count}/{totalLevelCount} 个并发档位",
            Error: null,
            PracticalConcurrencyLimit: null);

        ApplyConcurrencyResults(result);
        UpdateKpiLabels(SelectedTestMode);
    }

    internal static ConcurrencyLevelResult BuildConcurrencyLevelResult(
        ProxyConcurrencyPressureStageResult stage,
        int? stableConcurrencyLimit,
        int? rateLimitStartConcurrency,
        int? highRiskConcurrency,
        double maxThroughput,
        double maxP50Latency,
        double maxP95Ttft)
    {
        var successRate = stage.TotalRequests == 0
            ? 0
            : stage.SuccessCount * 100d / stage.TotalRequests;
        var errorRate = 100d - successRate;
        var throughput = stage.AverageTokensPerSecond ?? 0;
        var isHighRisk = highRiskConcurrency == stage.Concurrency ||
                         successRate < 80d ||
                         stage.TimeoutCount > 0 ||
                         stage.ServerErrorCount > 0;
        var isRateLimitStart = rateLimitStartConcurrency == stage.Concurrency ||
                               stage.RateLimitedCount > 0;
        var isStableLimit = stableConcurrencyLimit == stage.Concurrency;
        var status = isHighRisk
            ? "高风险"
            : isRateLimitStart
                ? "限流"
                : isStableLimit
                    ? "稳定上限"
                    : successRate >= 95d && stage.TimeoutCount == 0 && stage.ServerErrorCount == 0
                        ? "稳定"
                        : "观察";

        return new ConcurrencyLevelResult
        {
            Level = stage.Concurrency,
            TotalRequests = stage.TotalRequests,
            SuccessCount = stage.SuccessCount,
            RateLimitedCount = stage.RateLimitedCount,
            ServerErrorCount = stage.ServerErrorCount,
            TimeoutCount = stage.TimeoutCount,
            Throughput = throughput,
            ErrorRate = errorRate,
            SuccessRate = successRate,
            P50ChatLatencyMs = stage.P50ChatLatencyMs,
            P95TtftMs = stage.P95TtftMs,
            AverageTokensPerSecond = stage.AverageTokensPerSecond,
            ThroughputRatio = NormalizeRatio(throughput, maxThroughput),
            P50LatencyRatio = NormalizeRatio(stage.P50ChatLatencyMs, maxP50Latency),
            P95TtftRatio = NormalizeRatio(stage.P95TtftMs, maxP95Ttft),
            IsStableLimit = isStableLimit,
            IsRateLimitStart = isRateLimitStart,
            IsHighRisk = isHighRisk,
            VerdictText = status,
            SummaryText = string.IsNullOrWhiteSpace(stage.Summary) ? status : stage.Summary,
            DetailText = BuildConcurrencyLevelDetailText(stage, status, isStableLimit, isRateLimitStart, isHighRisk, successRate, errorRate),
            SuccessRateText = $"{successRate:F1}%",
            ThroughputText = FormatTokensPerSecond(stage.AverageTokensPerSecond),
            ErrorRateText = $"{errorRate:F1}%",
            P50LatencyText = FormatNullableMilliseconds(stage.P50ChatLatencyMs),
            P95TtftText = FormatNullableMilliseconds(stage.P95TtftMs),
            StatusText = status,
            CompletedText = $"{stage.SuccessCount}/{stage.TotalRequests}",
            ErrorBreakdownText = $"429 {stage.RateLimitedCount} / 5xx {stage.ServerErrorCount} / 超时 {stage.TimeoutCount}",
            RequestMixText = $"{stage.SuccessCount}/{stage.TotalRequests} 成功，429 {stage.RateLimitedCount}，5xx {stage.ServerErrorCount}，超时 {stage.TimeoutCount}",
        };
    }

    private static string BuildConcurrencyLevelDetailText(
        ProxyConcurrencyPressureStageResult stage,
        string verdict,
        bool isStableLimit,
        bool isRateLimitStart,
        bool isHighRisk,
        double successRate,
        double errorRate)
    {
        var markers = new List<string>();
        if (isStableLimit) markers.Add("稳定上限");
        if (isRateLimitStart) markers.Add("限流起点");
        if (isHighRisk) markers.Add("高风险");

        return string.Join(
            "\n",
            $"结论：{verdict}",
            $"并发：x{stage.Concurrency}",
            $"请求：{stage.SuccessCount}/{stage.TotalRequests} 成功，429 {stage.RateLimitedCount}，5xx {stage.ServerErrorCount}，超时 {stage.TimeoutCount}",
                $"成功率：{successRate:F1}%，错误率：{errorRate:F1}%",
            $"P50 普通延迟：{FormatNullableMilliseconds(stage.P50ChatLatencyMs)}，P95 TTFT：{FormatNullableMilliseconds(stage.P95TtftMs)}，吞吐：{FormatTokensPerSecond(stage.AverageTokensPerSecond)}",
            $"边界标记：{(markers.Count == 0 ? "无" : string.Join(" / ", markers))}",
            $"摘要：{(string.IsNullOrWhiteSpace(stage.Summary) ? "--" : stage.Summary)}");
    }

    private static double NormalizeRatio(double? value, double maxValue)
        => value.HasValue && maxValue > 0d
            ? Math.Clamp(value.Value / maxValue * 100d, 0d, 100d)
            : 0d;

    private static string FormatConcurrencyLevel(int? level)
        => level.HasValue ? $"x{level.Value}" : "0";

    private async Task<string> ResolveCachedConversationWireApiAsync(
        ProxyEndpointSettings settings,
        CancellationToken cancellationToken)
    {
        try
        {
            var cached = await _modelCacheService.TryResolvePreferredWireApiAsync(
                settings.BaseUrl,
                settings.ApiKey,
                settings.Model,
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(cached))
            {
                return cached;
            }
        }
        catch
        {
            // Cache lookup is best-effort; do not trigger a live protocol probe from test startup.
        }

        return InferWireApiFromModelName(settings.Model) ??
               ProxyWireApiProbeService.ChatCompletionsWireApi;
    }

    private static string? InferWireApiFromModelName(string? model)
    {
        var normalized = (model ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        if (normalized.Contains("claude", StringComparison.Ordinal) ||
            normalized.Contains("anthropic", StringComparison.Ordinal))
        {
            return ProxyWireApiProbeService.AnthropicMessagesWireApi;
        }

        if (normalized.Contains("responses", StringComparison.Ordinal))
        {
            return ProxyWireApiProbeService.ResponsesWireApi;
        }

        return null;
    }

    private void BuildConcurrencyChart(List<ConcurrencyLevelResult> results)
    {
        LiveChartsInitializer.EnsureInitialized();
        var theme = GetChartTheme();
        var colors = ChartPalette.ForTheme(theme);
        var labelPaint = ChartPalette.LegendPaint(theme);

        var throughputs = results.Select(r => r.Throughput).ToArray();
        var errorRates = results.Select(r => r.ErrorRate).ToArray();
        var successRates = results.Select(r => r.SuccessRate).ToArray();
        var labels = results.Select(r => $"{r.Level}").ToArray();

        ConcurrencyChartSeries =
        [
            new ColumnSeries<double>
            {
                Values = throughputs,
                Name = "Throughput (tok/s)",
                Fill = new SolidColorPaint(new SKColor(0x2F, 0x7D, 0xFF, 0xD8)),
                Stroke = null,
                MaxBarWidth = 34,
                ScalesYAt = 0,
            },
            new LineSeries<double>
            {
                Values = successRates,
                Name = "成功率 (%)",
                GeometrySize = 7,
                LineSmoothness = 0.35,
                Stroke = new SolidColorPaint(new SKColor(0x22, 0xA0, 0x6B)) { StrokeThickness = 2 },
                Fill = null,
                GeometryFill = new SolidColorPaint(new SKColor(0x22, 0xA0, 0x6B)),
                GeometryStroke = new SolidColorPaint(SKColors.White) { StrokeThickness = 1 },
                ScalesYAt = 1,
            },
            new LineSeries<double>
            {
                Values = errorRates,
                Name = "错误率 (%)",
                GeometrySize = 7,
                LineSmoothness = 0.35,
                Stroke = new SolidColorPaint(new SKColor(0xDC, 0x26, 0x26)) { StrokeThickness = 2 },
                Fill = null,
                GeometryFill = new SolidColorPaint(new SKColor(0xDC, 0x26, 0x26)),
                GeometryStroke = new SolidColorPaint(SKColors.White) { StrokeThickness = 1 },
                ScalesYAt = 1,
            },
        ];

        ConcurrencyChartXAxes =
        [
            new Axis
            {
                Name = "并发档位",
                Labels = labels,
                NamePaint = labelPaint,
                LabelsPaint = labelPaint,
            },
        ];

        ConcurrencyChartYAxes =
        [
            new Axis
            {
                Name = "Throughput (tok/s)",
                NamePaint = labelPaint,
                LabelsPaint = labelPaint,
                Position = LiveChartsCore.Measure.AxisPosition.Start,
            },
            new Axis
            {
                Name = "成功率 / 错误率 (%)",
                MinLimit = 0,
                MaxLimit = 100,
                NamePaint = labelPaint,
                LabelsPaint = labelPaint,
                Position = LiveChartsCore.Measure.AxisPosition.End,
            },
        ];
    }

}
