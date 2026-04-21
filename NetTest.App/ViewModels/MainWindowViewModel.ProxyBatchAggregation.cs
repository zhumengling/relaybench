using System.Text;
using NetTest.App.Services;
using NetTest.Core.Models;
using NetTest.Core.Support;

namespace NetTest.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private sealed record ProxyBatchAggregateRow(
        ProxyBatchTargetEntry Entry,
        int RunCount,
        int FullPassRounds,
        double AveragePassedCapabilityCount,
        double? AverageChatLatencyMs,
        double? AverageTtftMs,
        double? AverageBenchmarkTokensPerSecond,
        int LongStreamingExecutedRounds,
        int LongStreamingPassRounds,
        double CompositeScore,
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
                var latest = ordered[^1].Result;
                var averagePassed = ordered.Average(row => ResolveBatchPassedCapabilityCount(row.Result));
                var averageChatLatency = Average(ordered.Select(row => row.Result.ChatLatency?.TotalMilliseconds));
                var averageTtft = Average(ordered.Select(row => row.Result.StreamFirstTokenLatency?.TotalMilliseconds));
                var averageBenchmarkTokensPerSecond = Average(ordered.Select(row =>
                    row.Result.ThroughputBenchmarkResult?.MedianOutputTokensPerSecond));
                var fullPassRounds = ordered.Count(row => ResolveBatchPassedCapabilityCount(row.Result) == 5);
                var longStreamingExecutedRounds = ordered.Count(row => row.Result.LongStreamingResult is not null);
                var longStreamingPassRounds = ordered.Count(row => row.Result.LongStreamingResult?.Success == true);
                return new
                {
                    Entry = group.Key,
                    RunCount = ordered.Length,
                    FullPassRounds = fullPassRounds,
                    AveragePassedCapabilityCount = averagePassed,
                    AverageChatLatencyMs = averageChatLatency,
                    AverageTtftMs = averageTtft,
                    AverageBenchmarkTokensPerSecond = averageBenchmarkTokensPerSecond,
                    LongStreamingExecutedRounds = longStreamingExecutedRounds,
                    LongStreamingPassRounds = longStreamingPassRounds,
                    LatestResult = latest
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
                row.LatestResult))
            .ToArray();
    }

    private static IOrderedEnumerable<ProxyBatchAggregateRow> OrderBatchAggregateRows(IEnumerable<ProxyBatchAggregateRow> rows)
        => rows
            .OrderByDescending(row => row.CompositeScore)
            .ThenBy(row => row.AverageChatLatencyMs ?? double.MaxValue)
            .ThenBy(row => row.AverageTtftMs ?? double.MaxValue)
            .ThenByDescending(row => row.AverageBenchmarkTokensPerSecond ?? double.MinValue)
            .ThenBy(row => row.Entry.Name, StringComparer.OrdinalIgnoreCase);

    private static int ResolveBatchPassedCapabilityCount(ProxyDiagnosticsResult result)
        => new[]
        {
            result.ModelsRequestSucceeded,
            result.ChatRequestSucceeded,
            result.StreamRequestSucceeded,
            FindScenario(GetScenarioResults(result), ProxyProbeScenarioKind.Responses)?.Success == true,
            FindScenario(GetScenarioResults(result), ProxyProbeScenarioKind.StructuredOutput)?.Success == true
        }.Count(value => value);

    private static string BuildBatchCapabilityMatrix(ProxyDiagnosticsResult result)
        => string.Join(
            "，",
            new[]
            {
                $"/models {(result.ModelsRequestSucceeded ? "成功" : "失败")}",
                $"普通对话 {(result.ChatRequestSucceeded ? "成功" : "失败")}",
                $"流式对话 {(result.StreamRequestSucceeded ? "成功" : "失败")}",
                $"Responses {(FindScenario(GetScenarioResults(result), ProxyProbeScenarioKind.Responses)?.Success == true ? "成功" : "失败")}",
                $"结构化输出 {(FindScenario(GetScenarioResults(result), ProxyProbeScenarioKind.StructuredOutput)?.Success == true ? "成功" : "失败")}"
            });

    private static string BuildBatchStabilityLabel(ProxyBatchAggregateRow row)
    {
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
        => $"{BuildBatchStabilityLabel(row)} | {BuildBatchCapabilityBreakdown(row, includeDeepHint: true)}";

    private static ProxyBatchComparisonChartItem[] CreateProxyBatchComparisonChartItems(IReadOnlyList<ProxyBatchAggregateRow> rows)
        => rows
            .Select((row, index) => new ProxyBatchComparisonChartItem(
                index + 1,
                row.Entry.Name,
                row.Entry.BaseUrl,
                row.CompositeScore,
                $"{row.CompositeScore:F1} 分",
                ResolveBatchAggregateStabilityRatio(row),
                BuildBatchStabilityLabel(row),
                row.AverageTtftMs,
                row.AverageChatLatencyMs,
                row.AverageBenchmarkTokensPerSecond,
                row.LatestResult.Verdict ?? "待复核",
                BuildProxyBatchAggregateSecondaryText(row),
                row.RunCount))
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
            builder.AppendLine(
                $"综合分：{item.value.CompositeScore:F1}；{BuildBatchStabilityLabel(item.value)}；{BuildBatchCapabilityBreakdown(item.value, includeDeepHint: true)}；满 5 项 {item.value.FullPassRounds}/{item.value.RunCount} 轮；平均普通 {FormatMillisecondsValue(item.value.AverageChatLatencyMs)}；平均 TTFT {FormatMillisecondsValue(item.value.AverageTtftMs)}；独立吞吐 {FormatTokensPerSecond(item.value.AverageBenchmarkTokensPerSecond)}");
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
            builder.AppendLine($"累计轮次：{item.value.RunCount}");
            builder.AppendLine($"稳定性结论：{BuildBatchStabilityLabel(item.value)}");
            builder.AppendLine($"综合分：{item.value.CompositeScore:F1}");
            builder.AppendLine($"能力均值：{FormatBatchDisplayedCapabilityAverage(item.value)}");
            builder.AppendLine($"基础均值：{FormatCapabilityAverage(item.value.AveragePassedCapabilityCount)}/5");
            builder.AppendLine($"满 5 项轮次：{item.value.FullPassRounds}/{item.value.RunCount}");
            builder.AppendLine($"平均普通对话：{FormatMillisecondsValue(item.value.AverageChatLatencyMs)}");
            builder.AppendLine($"平均 TTFT：{FormatMillisecondsValue(item.value.AverageTtftMs)}");
            builder.AppendLine($"平均独立吞吐：{FormatTokensPerSecond(item.value.AverageBenchmarkTokensPerSecond)}");
            builder.AppendLine(item.value.LongStreamingExecutedRounds > 0
                ? $"增强长流：{item.value.LongStreamingPassRounds}/{item.value.LongStreamingExecutedRounds} 轮通过"
                : "增强长流：未执行");
            builder.AppendLine("深度测试：入口组模式不聚合，需查看单次诊断图表。");
            builder.AppendLine($"最近一轮时间：{item.value.LatestResult.CheckedAt:yyyy-MM-dd HH:mm:ss}");
            builder.AppendLine($"最近一轮判定：{item.value.LatestResult.Verdict ?? "待复核"}");
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
}
