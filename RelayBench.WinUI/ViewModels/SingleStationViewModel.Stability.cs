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
{    // ========== Stability Mode ==========
    private async Task RunStabilityModeAsync(CancellationToken ct)
    {
        var rounds = GetStabilityRoundCount();
        var delayMilliseconds = GetStabilityDelayMilliseconds();
        StatusText = $"正在运行稳定性测试（{rounds} 轮）...";
        HasStabilityResults = false;
        StabilityCompletedRounds = 0;
        StabilityTotalRounds = rounds;
        RefreshStabilityTrendRows([], rounds, isRunning: true);

        var settings = BuildSettings();
        var completedRounds = new List<ProxyDiagnosticsResult>();
        var progress = new Progress<string>(msg => StatusText = msg);
        var roundProgress = new Progress<ProxyDiagnosticsResult>(roundResult =>
        {
            completedRounds.Add(roundResult);
            StabilityCompletedRounds = completedRounds.Count;
            ApplyPartialStabilityResultsCore(completedRounds, isRunning: completedRounds.Count < rounds);
            ApplyCommonDiagnosticsDetails(roundResult, "Stability live");
            StatusText = $"稳定性：第 {StabilityCompletedRounds}/{StabilityTotalRounds} 轮";
            UpdateKpiLabels(SelectedTestMode);
        });

        try
        {
            var result = await _diagnosticsService.RunSeriesAsync(
                settings,
                requestedRounds: rounds,
                delayMilliseconds: delayMilliseconds,
                progress: progress,
                roundProgress: roundProgress,
                cancellationToken: ct,
                includeSemanticStabilityProbes: EnableSemanticStabilitySampling);

            ApplyStabilityResult(result);
            if (result.RoundResults.LastOrDefault() is { } latestRound)
            {
                ApplyCommonDiagnosticsDetails(latestRound, "Stability");
            }
        }
        catch (OperationCanceledException) when (completedRounds.Count > 0)
        {
            // Display partial results from rounds completed before cancellation
            ApplyPartialStabilityResultsCore(completedRounds, isRunning: false);
            StatusText = $"稳定性测试已取消，已显示 {completedRounds.Count}/{StabilityTotalRounds} 轮结果";
            throw; // Re-throw so StartTestAsync knows it was cancelled
        }
    }

    private void ApplyPartialStabilityResultsCore(List<ProxyDiagnosticsResult> completedRounds, bool isRunning)
    {
        var chatLatencies = completedRounds
            .Where(r => r.ChatLatency.HasValue)
            .Select(r => r.ChatLatency!.Value.TotalMilliseconds)
            .OrderBy(x => x)
            .ToList();

        if (chatLatencies.Count > 0)
        {
            StabilityP50 = $"{Percentile(chatLatencies, 0.50):F0} ms";
            StabilityP95 = $"{Percentile(chatLatencies, 0.95):F0} ms";
            StabilityP99 = $"{Percentile(chatLatencies, 0.99):F0} ms";
        }
        else
        {
            StabilityP50 = "0 ms";
            StabilityP95 = "0 ms";
            StabilityP99 = "0 ms";
        }

        HasStabilityResults = true;
        BuildStabilityRoundChart(completedRounds);
        RefreshStabilityTrendRows(completedRounds, StabilityTotalRounds, isRunning);

        var successCount = completedRounds.Count(r => r.ChatRequestSucceeded);
        var successRate = completedRounds.Count > 0 ? (double)successCount / completedRounds.Count * 100 : 0;
        StabilityHealthScore = "0/100";
        StabilitySuccessRate = $"{successRate:F1}%";
        StabilitySummary = $"已完成 {completedRounds.Count}/{StabilityTotalRounds} 轮，当前成功率 {successRate:F1}%";
        StabilityCompletedRounds = completedRounds.Count;
        HasStabilityResults = true;
    }

    private void ApplyStabilityResult(ProxyStabilityResult result)
    {
        // Compute percentiles from round chat latencies
        var chatLatencies = result.RoundResults
            .Where(r => r.ChatLatency.HasValue)
            .Select(r => r.ChatLatency!.Value.TotalMilliseconds)
            .OrderBy(x => x)
            .ToList();

        if (chatLatencies.Count > 0)
        {
            StabilityP50 = $"{Percentile(chatLatencies, 0.50):F0} ms";
            StabilityP95 = $"{Percentile(chatLatencies, 0.95):F0} ms";
            StabilityP99 = $"{Percentile(chatLatencies, 0.99):F0} ms";

        }
        else
        {
            StabilityP50 = "0 ms";
            StabilityP95 = "0 ms";
            StabilityP99 = "0 ms";
        }

        HasStabilityResults = true;
        BuildStabilityRoundChart(result.RoundResults);
        RefreshStabilityTrendRows(result.RoundResults, result.RequestedRounds, isRunning: false);
        StabilityHealthScore = $"{result.HealthScore}/100";
        StabilitySuccessRate = $"{result.FullSuccessRate:F1}%";
        StabilitySummary = result.Summary;
        StabilityCompletedRounds = result.CompletedRounds;

        StatusText = $"稳定性测试完成：{result.HealthLabel}";
    }

    private void BuildStabilityRoundChart(IReadOnlyList<ProxyDiagnosticsResult> rounds)
    {
        LiveChartsInitializer.EnsureInitialized();
        var theme = GetChartTheme();
        var colors = ChartPalette.ForTheme(theme);
        var labelPaint = ChartPalette.LegendPaint(theme);
        var chatLatencies = rounds
            .Select(static round => round.ChatLatency?.TotalMilliseconds ?? 0d)
            .ToArray();
        var ttftLatencies = rounds
            .Select(static round => round.StreamFirstTokenLatency?.TotalMilliseconds ?? 0d)
            .ToArray();

        StabilityChartSeries =
        [
            new ColumnSeries<double>
            {
                Values = chatLatencies,
                Name = "聊天延迟",
                Fill = new SolidColorPaint(colors[0]),
                Stroke = null,
                MaxBarWidth = 34,
            },
            new LineSeries<double>
            {
                Values = ttftLatencies,
                Name = "首 token",
                GeometrySize = 0,
                LineSmoothness = 0.45,
                Stroke = new SolidColorPaint(colors[1]) { StrokeThickness = 2 },
                Fill = null,
            },
        ];

        StabilityChartYAxes =
        [
            new Axis
            {
                Name = "延迟 (ms)",
                NamePaint = labelPaint,
                LabelsPaint = labelPaint,
            },
        ];

        StabilityChartXAxes =
        [
            new Axis
            {
                Labels = Enumerable.Range(1, rounds.Count).Select(static i => $"第{i}轮").ToArray(),
                LabelsPaint = labelPaint,
            },
        ];
    }

    private void RefreshStabilityTrendRows(
        IReadOnlyList<ProxyDiagnosticsResult> rounds,
        int requestedRounds,
        bool isRunning)
    {
        StabilityTrendRows.Clear();
        foreach (var row in BuildStabilityTrendRows(rounds, requestedRounds, isRunning))
        {
            StabilityTrendRows.Add(row);
        }
    }

    internal static IReadOnlyList<StabilityTrendRow> BuildStabilityTrendRows(
        IReadOnlyList<ProxyDiagnosticsResult> rounds,
        int requestedRounds,
        bool isRunning)
    {
        if (rounds.Count == 0)
        {
            return
            [
                CreateStabilityTrendPlaceholder("稳定性", "0%", requestedRounds, isRunning, higherIsBetter: true),
                CreateStabilityTrendPlaceholder("普通延迟", "--", requestedRounds, isRunning, higherIsBetter: false),
                CreateStabilityTrendPlaceholder("TTFT", "--", requestedRounds, isRunning, higherIsBetter: false),
            ];
        }

        var completedCount = rounds.Count;
        var successValues = rounds
            .Select(static round => IsFullStabilityRoundSuccess(round) ? 100d : ComputeStabilityComponentSuccessRate(round))
            .Cast<double?>()
            .ToArray();
        var chatValues = rounds
            .Select(static round => round.ChatLatency?.TotalMilliseconds)
            .ToArray();
        var ttftValues = rounds
            .Select(static round => round.StreamFirstTokenLatency?.TotalMilliseconds)
            .ToArray();
        var latestSuccess = successValues.LastOrDefault() ?? 0d;
        var failedRounds = successValues.Count(static value => value is < 100d);
        var stabilitySummary = BuildStabilityPercentSummary(successValues);
        var stabilityTone = latestSuccess >= 80d ? "Success" : "Warn";

        return
        [
            new StabilityTrendRow
            {
                Title = "稳定性",
                ValueText = $"{latestSuccess:0.#}%",
                StatusText = latestSuccess >= 100d
                    ? "全部通过"
                    : failedRounds > 0 ? $"{failedRounds} 轮异常" : "部分通过",
                HintText = BuildStabilityProgressHint(completedCount, requestedRounds),
                ValueSummaryText = stabilitySummary,
                DetailText = BuildStabilityTrendDetailText(
                    "稳定性",
                    $"{latestSuccess:0.#}%",
                    completedCount,
                    requestedRounds,
                    stabilitySummary,
                    stabilityTone),
                IsRunning = isRunning,
                Tone = stabilityTone,
            },
            CreateLatencyTrendRow(
                "普通延迟",
                chatValues,
                completedCount,
                requestedRounds,
                isRunning),
            CreateLatencyTrendRow(
                "TTFT",
                ttftValues,
                completedCount,
                requestedRounds,
                isRunning),
        ];
    }

    private static StabilityTrendRow CreateStabilityTrendPlaceholder(
        string title,
        string valueText,
        int requestedRounds,
        bool isRunning,
        bool higherIsBetter)
        => new()
        {
            Title = title,
            ValueText = valueText,
            StatusText = "等待结果",
            HintText = title == "稳定性"
                ? BuildStabilityProgressHint(0, requestedRounds)
                : higherIsBetter ? "暂无有效数据 | 越高越好" : "暂无有效数据 | 越低越好",
            ValueSummaryText = "尚未完成首轮",
            DetailText = $"{title}：等待首轮稳定性巡检结果。",
            IsRunning = isRunning,
            Tone = "Neutral",
        };

    private static StabilityTrendRow CreateLatencyTrendRow(
        string title,
        IReadOnlyList<double?> values,
        int completedCount,
        int requestedRounds,
        bool isRunning)
    {
        var latest = values.LastOrDefault(static value => value is > 0d);
        var valueText = latest.HasValue ? $"{latest.Value:0} ms" : "--";
        var status = BuildStabilityLatencyStatus(values);
        var summary = BuildStabilityMillisecondsSummary(values);
        var tone = status == "波动偏高" ? "Warn" : latest.HasValue ? "Success" : "Neutral";

        return new StabilityTrendRow
        {
            Title = title,
            ValueText = valueText,
            StatusText = status,
            HintText = $"{BuildStabilityProgressHint(completedCount, requestedRounds)} | 越低越好",
            ValueSummaryText = summary,
            DetailText = BuildStabilityTrendDetailText(title, valueText, completedCount, requestedRounds, summary, tone),
            IsRunning = isRunning,
            Tone = tone,
        };
    }

    private static string BuildStabilityTrendDetailText(
        string title,
        string valueText,
        int completedCount,
        int requestedRounds,
        string summary,
        string tone)
        => $"指标：{title}\n当前：{valueText}\n轮次：{completedCount}/{Math.Max(requestedRounds, completedCount)}\n摘要：{summary}\n状态：{tone}";

    private static string BuildStabilityProgressHint(int completedCount, int requestedRounds)
        => requestedRounds > 0
            ? $"已完成 {completedCount}/{requestedRounds} 轮"
            : $"已完成 {completedCount} 轮";

    private static string BuildStabilityPercentSummary(IEnumerable<double?> values)
    {
        var numericValues = values
            .Where(static value => value is >= 0d)
            .Select(static value => value!.Value)
            .ToArray();

        return numericValues.Length == 0
            ? "暂无有效数据"
            : $"最低 {numericValues.Min():0.#}% · 最高 {numericValues.Max():0.#}% · 平均 {numericValues.Average():0.#}%";
    }

    private static string BuildStabilityMillisecondsSummary(IEnumerable<double?> values)
    {
        var numericValues = values
            .Where(static value => value is > 0d)
            .Select(static value => value!.Value)
            .ToArray();

        return numericValues.Length == 0
            ? "暂无有效数据"
            : $"最低 {numericValues.Min():0} ms · 最高 {numericValues.Max():0} ms · 平均 {numericValues.Average():0} ms";
    }

    private static string BuildStabilityLatencyStatus(IEnumerable<double?> values)
    {
        var numericValues = values
            .Where(static value => value is > 0d)
            .Select(static value => value!.Value)
            .ToArray();

        if (numericValues.Length < 2)
        {
            return numericValues.Length == 0 ? "等待结果" : "首轮结果";
        }

        var average = numericValues.Average();
        if (average <= 0)
        {
            return "等待结果";
        }

        var swing = (numericValues.Max() - numericValues.Min()) / average;
        return swing switch
        {
            < 0.18d => "稳定",
            < 0.35d => "轻微波动",
            _ => "波动偏高",
        };
    }

    private static bool IsFullStabilityRoundSuccess(ProxyDiagnosticsResult result)
        => result.ModelsRequestSucceeded &&
           result.ChatRequestSucceeded &&
           result.StreamRequestSucceeded;

    private static double ComputeStabilityComponentSuccessRate(ProxyDiagnosticsResult result)
    {
        var score = 0d;
        score += result.ModelsRequestSucceeded ? 100d / 3d : 0d;
        score += result.ChatRequestSucceeded ? 100d / 3d : 0d;
        score += result.StreamRequestSucceeded ? 100d / 3d : 0d;
        return Math.Round(score, 1);
    }

}
