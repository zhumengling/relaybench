using System.Text;
using RelayBench.App.Services;
using RelayBench.Core.Models;
using RelayBench.Core.Support;

namespace RelayBench.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private sealed record ProxyBatchAggregateRow(
        ProxyBatchTargetEntry Entry,
        int RunCount,
        int DisplaySampleCount,
        int FullPassRounds,
        double AveragePassedCapabilityCount,
        double? AverageChatLatencyMs,
        double? AverageTtftMs,
        double? AverageBenchmarkTokensPerSecond,
        int LongStreamingExecutedRounds,
        int LongStreamingPassRounds,
        double CompositeScore,
        ProxyBatchProbeStage LatestStage,
        int LatestCompletedBaselineCount,
        int LatestTotalBaselineCount,
        bool LatestIsPlaceholder,
        string? LatestPlaceholderMessage,
        ProxyDiagnosticsResult LatestResult);

    private static IReadOnlyList<ProxyBatchAggregateRow> BuildProxyBatchAggregateRows(
        IEnumerable<IReadOnlyList<ProxyBatchProbeRow>> completedRuns,
        IReadOnlyList<ProxyBatchProbeRow>? currentRunRows = null)
    {
        IEnumerable<ProxyBatchProbeRow> allRows = completedRuns.SelectMany(run => run);
        if (currentRunRows is { Count: > 0 })
        {
            allRows = allRows.Concat(currentRunRows);
        }

        var materialized = allRows
            .GroupBy(row => row.Entry)
            .Select(group =>
            {
                var ordered = group
                    .OrderBy(row => row.Result.CheckedAt)
                    .ToArray();
                var displayRows = ordered
                    .Where(row => !row.IsPlaceholder)
                    .ToArray();
                var latest = ordered[^1];
                var averagePassed = displayRows.Length == 0
                    ? 0d
                    : displayRows.Average(row => ResolveBatchPassedCapabilityCount(row.Result));
                var averageChatLatency = Average(displayRows.Select(row => row.Result.ChatLatency?.TotalMilliseconds));
                var averageTtft = Average(displayRows.Select(row => row.Result.StreamFirstTokenLatency?.TotalMilliseconds));
                var averageBenchmarkTokensPerSecond = Average(displayRows.Select(row =>
                    row.Result.ThroughputBenchmarkResult?.MedianOutputTokensPerSecond));
                var fullPassRounds = displayRows.Count(row =>
                    row.Stage == ProxyBatchProbeStage.Completed &&
                    ResolveBatchPassedCapabilityCount(row.Result) == 5);
                var longStreamingExecutedRounds = displayRows.Count(row => row.Result.LongStreamingResult is not null);
                var longStreamingPassRounds = displayRows.Count(row => row.Result.LongStreamingResult?.Success == true);
                return new
                {
                    Entry = group.Key,
                    RunCount = displayRows.Count(row => row.Stage == ProxyBatchProbeStage.Completed),
                    DisplaySampleCount = ordered.Length,
                    FullPassRounds = fullPassRounds,
                    AveragePassedCapabilityCount = averagePassed,
                    AverageChatLatencyMs = averageChatLatency,
                    AverageTtftMs = averageTtft,
                    AverageBenchmarkTokensPerSecond = averageBenchmarkTokensPerSecond,
                    LongStreamingExecutedRounds = longStreamingExecutedRounds,
                    LongStreamingPassRounds = longStreamingPassRounds,
                    LatestStage = latest.Stage,
                    LatestCompletedBaselineCount = latest.CompletedBaselineScenarioCount,
                    LatestTotalBaselineCount = latest.TotalBaselineScenarioCount,
                    LatestIsPlaceholder = latest.IsPlaceholder,
                    LatestPlaceholderMessage = latest.PlaceholderMessage,
                    LatestResult = latest.Result
                };
            })
            .ToArray();

        var chatLatencyRange = ResolveMetricRange(materialized.Select(static row => row.AverageChatLatencyMs));
        var ttftRange = ResolveMetricRange(materialized.Select(static row => row.AverageTtftMs));
        var throughputRange = ResolveMetricRange(materialized.Select(static row => row.AverageBenchmarkTokensPerSecond));

        return materialized
            .Select(row => new ProxyBatchAggregateRow(
                row.Entry,
                row.RunCount,
                row.DisplaySampleCount,
                row.FullPassRounds,
                row.AveragePassedCapabilityCount,
                row.AverageChatLatencyMs,
                row.AverageTtftMs,
                row.AverageBenchmarkTokensPerSecond,
                row.LongStreamingExecutedRounds,
                row.LongStreamingPassRounds,
                ProxyCompositeMetricScoreCalculator.CalculateCompositeScore(
                    row.AverageChatLatencyMs,
                    row.AverageTtftMs,
                    row.AverageBenchmarkTokensPerSecond,
                    chatLatencyRange,
                    ttftRange,
                    throughputRange),
                row.LatestStage,
                row.LatestCompletedBaselineCount,
                row.LatestTotalBaselineCount,
                row.LatestIsPlaceholder,
                row.LatestPlaceholderMessage,
                row.LatestResult))
            .ToArray();
    }

    private static IOrderedEnumerable<ProxyBatchAggregateRow> OrderBatchAggregateRows(IEnumerable<ProxyBatchAggregateRow> rows)
        => rows
            .OrderByDescending(ResolveBatchAggregateSortWeight)
            .ThenBy(row => row.AverageChatLatencyMs ?? double.MaxValue)
            .ThenBy(row => row.AverageTtftMs ?? double.MaxValue)
            .ThenByDescending(row => row.AverageBenchmarkTokensPerSecond ?? double.MinValue)
            .ThenBy(row => row.Entry.Name, StringComparer.OrdinalIgnoreCase);

    private static int ResolveBatchPassedCapabilityCount(ProxyDiagnosticsResult result)
        => GetOrderedScenarioDefinitions()
            .Count(definition => FindScenario(GetScenarioResults(result), definition.Kind)?.Success == true);

    private static int ResolveBatchExecutedCapabilityCount(ProxyDiagnosticsResult result)
        => GetOrderedScenarioDefinitions()
            .Count(definition => FindScenario(GetScenarioResults(result), definition.Kind) is not null);

    private static string BuildBatchCapabilityMatrix(ProxyDiagnosticsResult result)
        => string.Join(
            "，",
            new[]
            {
                $"/models {ResolveBatchCapabilityStatusText(result, ProxyProbeScenarioKind.Models)}",
                $"普通对话 {ResolveBatchCapabilityStatusText(result, ProxyProbeScenarioKind.ChatCompletions)}",
                $"流式对话 {ResolveBatchCapabilityStatusText(result, ProxyProbeScenarioKind.ChatCompletionsStream)}",
                $"Responses {ResolveBatchCapabilityStatusText(result, ProxyProbeScenarioKind.Responses)}",
                $"结构化输出 {ResolveBatchCapabilityStatusText(result, ProxyProbeScenarioKind.StructuredOutput)}"
            });

    private static string BuildBatchStabilityLabel(ProxyBatchAggregateRow row)
    {
        if (row.LatestStage != ProxyBatchProbeStage.Completed)
        {
            return "进行中";
        }

        var stabilityRatio = ResolveBatchAggregateStabilityRatio(row);
        if (stabilityRatio >= 80d &&
            (!row.AverageChatLatencyMs.HasValue || row.AverageChatLatencyMs.Value <= 1800) &&
            (!row.AverageTtftMs.HasValue || row.AverageTtftMs.Value <= 1800))
        {
            return "稳定";
        }

        if (stabilityRatio >= 60d)
        {
            return "可用";
        }

        return "待复核";
    }

    private static double ResolveBatchAggregateStabilityRatio(ProxyBatchAggregateRow row)
        => Math.Round(
            Math.Clamp(
                ResolveBatchDisplayedCapabilityAverage(row) / ResolveBatchDisplayedCapabilityMax(row),
                0d,
                1d) * 100d,
            1);

    private static int ResolveBatchDisplayedCapabilityMax(ProxyBatchAggregateRow row)
        => 5 + (row.LongStreamingExecutedRounds > 0 ? 1 : 0);

    private static double ResolveBatchDisplayedCapabilityAverage(ProxyBatchAggregateRow row)
        => row.AveragePassedCapabilityCount +
           (ResolveBatchLongStreamingPassRatio(row) ?? 0d);

    private static double? ResolveBatchLongStreamingPassRatio(ProxyBatchAggregateRow row)
        => row.LongStreamingExecutedRounds <= 0
            ? null
            : (double)row.LongStreamingPassRounds / row.LongStreamingExecutedRounds;

    private static string FormatBatchDisplayedCapabilityAverage(ProxyBatchAggregateRow row)
        => $"{FormatCapabilityAverage(ResolveBatchDisplayedCapabilityAverage(row))}/{ResolveBatchDisplayedCapabilityMax(row)}";

    private static string BuildBatchCapabilityBreakdown(ProxyBatchAggregateRow row, bool includeDeepHint)
    {
        if (row.LatestIsPlaceholder)
        {
            return row.LatestPlaceholderMessage ?? "等待开始";
        }

        if (row.LatestStage != ProxyBatchProbeStage.Completed)
        {
            var isLiveThroughput = row.LatestResult.ThroughputBenchmarkResult?.IsLive == true;
            var liveText = row.LatestStage switch
            {
                ProxyBatchProbeStage.Baseline => $"基 {row.LatestCompletedBaselineCount}/{row.LatestTotalBaselineCount} | 基测进行中",
                ProxyBatchProbeStage.Throughput => $"基 {row.LatestCompletedBaselineCount}/{row.LatestTotalBaselineCount} | {(isLiveThroughput ? "tok/s 进行中" : "tok/s 已出")}",
                _ => $"基 {row.LatestCompletedBaselineCount}/{row.LatestTotalBaselineCount} | 进行中"
            };
            return includeDeepHint
                ? $"{liveText} | 深度看单次"
                : liveText;
        }

        var enhancedText = row.LongStreamingExecutedRounds > 0
            ? $"增 {row.LongStreamingPassRounds}/{row.LongStreamingExecutedRounds}"
            : "增 --";
        var text = $"基 {FormatCapabilityAverage(row.AveragePassedCapabilityCount)}/5 | {enhancedText}";
        return includeDeepHint
            ? $"{text} | 深度看单次"
            : text;
    }

    private static string FormatCapabilityAverage(double value)
    {
        var rounded = Math.Round(value, 1);
        return Math.Abs(rounded - Math.Round(rounded)) < 0.05
            ? Math.Round(rounded).ToString("F0")
            : rounded.ToString("F1");
    }

    private static string BuildProxyBatchAggregateSecondaryText(ProxyBatchAggregateRow row)
        => row.LatestIsPlaceholder
            ? (row.LatestPlaceholderMessage ?? "等待开始")
            : $"{BuildBatchStabilityLabel(row)} | {BuildBatchCapabilityBreakdown(row, includeDeepHint: true)}";

    private static ProxyBatchComparisonChartItem[] CreateProxyBatchComparisonChartItems(IReadOnlyList<ProxyBatchAggregateRow> rows)
        => rows
            .Select((row, index) =>
            {
                var stabilityText = BuildBatchStabilityLabel(row);
                return new ProxyBatchComparisonChartItem(
                    index + 1,
                    row.Entry.Name,
                    row.Entry.BaseUrl,
                    ResolveBatchComparisonCompositeScore(row),
                    BuildBatchComparisonCompositeText(row),
                    ResolveBatchAggregateStabilityRatio(row),
                    stabilityText,
                    row.AverageTtftMs,
                    row.AverageChatLatencyMs,
                    row.AverageBenchmarkTokensPerSecond,
                    BuildBatchComparisonVerdict(row),
                    BuildProxyBatchAggregateSecondaryText(row),
                    row.RunCount);
            })
            .ToArray();

    private static string BuildProxyBatchTopSummary(IReadOnlyList<ProxyBatchAggregateRow> rows, int maxCount = 5)
        => string.Join(
            "\n",
            rows
                .Take(maxCount)
                .Select((row, index) =>
                    $"TOP {index + 1}  {row.Entry.Name}  |  平均普通 {FormatMillisecondsValue(row.AverageChatLatencyMs)}  |  独立吞吐 {FormatTokensPerSecond(row.AverageBenchmarkTokensPerSecond)}  |  平均 TTFT {FormatMillisecondsValue(row.AverageTtftMs)}  |  综合分 {row.CompositeScore:F1}  |  {BuildBatchCapabilityBreakdown(row, includeDeepHint: false)}"));

    private static string BuildProxyBatchCapabilitySummaryText(IReadOnlyList<ProxyBatchAggregateRow> rows, string heading)
    {
        StringBuilder builder = new();
        builder.AppendLine(heading);

        foreach (var item in rows.Take(5).Select((value, index) => new { value, index }))
        {
            builder.AppendLine($"TOP {item.index + 1}  {item.value.Entry.Name}");
            if (item.value.LatestIsPlaceholder && item.value.RunCount == 0)
            {
                builder.AppendLine($"当前状态：{item.value.LatestPlaceholderMessage ?? "等待开始"}；历史：尚无已完成轮次。");
                builder.AppendLine($"最近一轮五项：{BuildBatchCapabilityMatrix(item.value.LatestResult)}");
                if (item.index < Math.Min(rows.Count, 5) - 1)
                {
                    builder.AppendLine();
                }

                continue;
            }

            builder.AppendLine(
                $"综合分：{BuildBatchComparisonCompositeText(item.value)}；{BuildBatchStabilityLabel(item.value)}；{BuildBatchCapabilityBreakdown(item.value, includeDeepHint: true)}；满 5 项 {item.value.FullPassRounds}/{Math.Max(item.value.RunCount, 1)} 轮；平均普通 {FormatMillisecondsValue(item.value.AverageChatLatencyMs)}；平均 TTFT {FormatMillisecondsValue(item.value.AverageTtftMs)}；独立吞吐 {FormatTokensPerSecond(item.value.AverageBenchmarkTokensPerSecond)}");
            builder.AppendLine($"最近一轮五项：{BuildBatchCapabilityMatrix(item.value.LatestResult)}");
            if (item.value.LongStreamingExecutedRounds > 0)
            {
                builder.AppendLine($"增强项长流：{item.value.LongStreamingPassRounds}/{item.value.LongStreamingExecutedRounds} 轮通过");
            }

            if (item.index < Math.Min(rows.Count, 5) - 1)
            {
                builder.AppendLine();
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static string BuildProxyBatchCapabilityDetailText(IReadOnlyList<ProxyBatchAggregateRow> rows, string heading)
    {
        StringBuilder builder = new();
        builder.AppendLine(heading);

        foreach (var item in rows.Take(5).Select((value, index) => new { value, index }))
        {
            builder.AppendLine($"#{item.index + 1} {item.value.Entry.Name}");
            builder.AppendLine($"地址：{item.value.Entry.BaseUrl}");
            builder.AppendLine(item.value.LatestIsPlaceholder && item.value.RunCount == 0
                ? $"累计轮次：0（当前状态：{item.value.LatestPlaceholderMessage ?? "等待开始"}）"
                : item.value.LatestStage == ProxyBatchProbeStage.Completed
                ? $"累计轮次：{item.value.RunCount}"
                : $"累计轮次：{item.value.RunCount}（当前轮进行中，已出现阶段结果）");
            builder.AppendLine($"稳定性结论：{BuildBatchStabilityLabel(item.value)}");
            builder.AppendLine($"综合分：{BuildBatchComparisonCompositeText(item.value)}");
            builder.AppendLine($"能力均值：{FormatBatchDisplayedCapabilityAverage(item.value)}");
            builder.AppendLine($"基础均值：{FormatCapabilityAverage(item.value.AveragePassedCapabilityCount)}/5");
            builder.AppendLine($"满 5 项轮次：{item.value.FullPassRounds}/{Math.Max(item.value.RunCount, 1)}");
            builder.AppendLine($"平均普通对话：{FormatMillisecondsValue(item.value.AverageChatLatencyMs)}");
            builder.AppendLine($"平均 TTFT：{FormatMillisecondsValue(item.value.AverageTtftMs)}");
            builder.AppendLine($"平均独立吞吐：{FormatTokensPerSecond(item.value.AverageBenchmarkTokensPerSecond)}");
            builder.AppendLine(item.value.LongStreamingExecutedRounds > 0
                ? $"增强长流：{item.value.LongStreamingPassRounds}/{item.value.LongStreamingExecutedRounds} 轮通过"
                : "增强长流：未执行");
            builder.AppendLine("深度测试：入口组模式不聚合，需查看单次诊断图表。");
            builder.AppendLine($"最近一轮时间：{item.value.LatestResult.CheckedAt:yyyy-MM-dd HH:mm:ss}");
            builder.AppendLine($"最近一轮判定：{BuildBatchComparisonVerdict(item.value)}");
            builder.AppendLine($"最近一轮五项：{BuildBatchCapabilityMatrix(item.value.LatestResult)}");
            builder.AppendLine(BuildDialogCapabilityDetail(item.value.LatestResult));
            builder.AppendLine($"CDN / 边缘：{item.value.LatestResult.CdnSummary ?? "未识别"}");
            builder.AppendLine($"错误：{item.value.LatestResult.Error ?? "无"}");

            if (item.index < Math.Min(rows.Count, 5) - 1)
            {
                builder.AppendLine();
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static (double Min, double Max) ResolveMetricRange(IEnumerable<double?> values)
    {
        var materialized = values
            .Where(static value => value.HasValue)
            .Select(static value => value!.Value)
            .ToArray();

        if (materialized.Length == 0)
        {
            return (0d, 0d);
        }

        return (materialized.Min(), materialized.Max());
    }

    private static double ResolveBatchAggregateSortWeight(ProxyBatchAggregateRow row)
    {
        if (row.LatestIsPlaceholder)
        {
            return -1d;
        }

        if (row.LatestStage == ProxyBatchProbeStage.Completed)
        {
            return 10_000d + row.CompositeScore;
        }

        var throughputBonus = row.AverageBenchmarkTokensPerSecond.HasValue ? 10d : 0d;
        return (row.LatestCompletedBaselineCount * 100d) + throughputBonus;
    }

    private static string ResolveBatchCapabilityStatusText(ProxyDiagnosticsResult result, ProxyProbeScenarioKind scenarioKind)
    {
        var scenario = FindScenario(GetScenarioResults(result), scenarioKind);
        if (scenario is null)
        {
            return "待测";
        }

        return scenario.Success ? "成功" : "失败";
    }

    private static double ResolveBatchComparisonCompositeScore(ProxyBatchAggregateRow row)
    {
        if (row.LatestIsPlaceholder)
        {
            return 0d;
        }

        if (row.LatestStage == ProxyBatchProbeStage.Completed)
        {
            return row.CompositeScore;
        }

        if (row.LatestStage == ProxyBatchProbeStage.Throughput)
        {
            return 92d;
        }

        if (row.LatestTotalBaselineCount <= 0)
        {
            return 0d;
        }

        return Math.Round((double)row.LatestCompletedBaselineCount / row.LatestTotalBaselineCount * 100d, 1);
    }

    private static string BuildBatchComparisonCompositeText(ProxyBatchAggregateRow row)
        => row.LatestIsPlaceholder
            ? "等待中"
            : row.LatestStage switch
        {
            ProxyBatchProbeStage.Completed => $"{row.CompositeScore:F1} 分",
            ProxyBatchProbeStage.Throughput => row.LatestResult.ThroughputBenchmarkResult?.IsLive == true ? "tok/s 实时中" : "tok/s 已出",
            _ => $"基 {row.LatestCompletedBaselineCount}/{row.LatestTotalBaselineCount}"
        };

    private static string BuildBatchComparisonVerdict(ProxyBatchAggregateRow row)
        => row.LatestIsPlaceholder
            ? (row.LatestPlaceholderMessage ?? "等待开始")
            : row.LatestStage switch
        {
            ProxyBatchProbeStage.Baseline => $"基测 {row.LatestCompletedBaselineCount}/{row.LatestTotalBaselineCount}",
            ProxyBatchProbeStage.Throughput => row.LatestResult.ThroughputBenchmarkResult?.IsLive == true ? "独立吞吐进行中" : "独立吞吐已出",
            _ => row.LatestResult.Verdict ?? "待复核"
        };
}
