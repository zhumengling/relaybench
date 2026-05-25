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

public sealed partial class BatchComparisonViewModel
{
    private void ApplyZeroHistoryState()
    {
        TotalSites = 0;
        CompletedSites = 0;
        Progress = 0;
        EstimatedRemaining = "0s";
        TopSiteName = "0";
        TopSiteScore = 0;
        TopLatencyDisplay = "0 ms";
        TopSuccessRateDisplay = "0.0%";
        TopThroughputDisplay = "0 tok/s";
        ActiveConnections = 0;
        IsRateLimited = false;
        SiteRankings.Clear();
        DeepTestQueue.Clear();
        RunTimeline.Clear();
        _lastTimelineKey = null;
        _historicalBatchTrend = [BatchCompositeTrendSnapshot.Zero];
        BuildComparisonCharts([]);
        BuildCompositeTrend(_historicalBatchTrend);
        BuildHeatmap([]);
    }

    private void LoadHistoricalBatchState()
    {
        try
        {
            var summaries = _historyRepository
                .QueryAsync(new HistoryQuery(TestType: "批量评测", Limit: 14))
                .GetAwaiter()
                .GetResult();
            if (summaries.Count == 0)
            {
                _historicalBatchTrend = [BatchCompositeTrendSnapshot.Zero];
                BuildCompositeTrend(_historicalBatchTrend);
                StatusText = "暂无History批量评测数据，当前显示 0";
                return;
            }

            var orderedSummaries = summaries
                .OrderBy(static item => item.CreatedAtUtc)
                .ToArray();
            var reports = new Dictionary<string, HistoryReport>(StringComparer.OrdinalIgnoreCase);
            foreach (var summary in orderedSummaries)
            {
                var report = _historyRepository.GetAsync(summary.RunId).GetAwaiter().GetResult();
                if (report is not null)
                {
                    reports[summary.RunId] = report;
                }
            }

            _historicalBatchTrend = BuildHistoricalBatchTrend(orderedSummaries, reports);
            BuildCompositeTrend(_historicalBatchTrend);

            var latestSummary = summaries.FirstOrDefault();
            if (latestSummary is null || !reports.TryGetValue(latestSummary.RunId, out var latest))
            {
                StatusText = "暂无History批量评测数据，当前显示 0";
                return;
            }

            var results = ReadBatchHistoryResults(latest.PayloadJson);
            if (results.Count == 0)
            {
                StatusText = "暂无History批量评测明细，当前显示 0";
                return;
            }

            TotalSites = results.Count;
            CompletedSites = results.Count;
            Progress = 100;
            EstimatedRemaining = "0s";
            UpdateRealtimeBatchResults(results, useHistoricalCompositeTrend: true);
            RunTimeline.Clear();
            _lastTimelineKey = null;
            AddTimelineItem("History批量", "History结果", $"已加载 {results.Count} 个Site的History结果", "Info");

            var top = results.OrderByDescending(static item => item.Score).First();
            TopSiteName = top.Name;
            TopSiteScore = top.Score;
            TopLatencyDisplay = $"{top.LatencyMs:F0} ms";
            TopSuccessRateDisplay = $"{top.SuccessRate:F1}%";
            TopThroughputDisplay = $"{top.Throughput:F1} tok/s";

            DeepTestQueue.Clear();
            foreach (var item in results.OrderByDescending(static item => item.Score))
            {
                var passed = item.SuccessRate >= 99 ? 5 : Math.Max(0, (int)Math.Round(item.SuccessRate / 20.0));
                var total = 5;
                var queueItem = new DeepTestQueueItem(item.Name, DeepTestStatus.Completed, passed, Math.Max(0, total - passed))
                {
                    CurrentStage = "历史结果",
                    RoundText = $"{passed}/{total}",
                    LatestResult = $"延迟 {item.LatencyMs:F0} ms | TTFT {(item.TtftMs.HasValue ? $"{item.TtftMs.Value:F0} ms" : "--")} | 成功率 {item.SuccessRate:F1}%",
                    ScoreText = $"{item.Score:F1}",
                    ProgressValue = passed,
                    ProgressMaximum = total,
                };
                AddQueueTimelineItem(queueItem, "历史结果", queueItem.LatestResult, "Info", includeGlobal: false);
                DeepTestQueue.Add(queueItem);
            }

            StatusText = $"已加载历史批量评测：{latest.CreatedAtUtc.ToLocalTime():MM-dd HH:mm}";
        }
        catch (Exception ex)
        {
            ApplyZeroHistoryState();
            StatusText = $"历史批量数据读取失败，当前显示 0: {ex.Message}";
        }
    }

    internal static List<BatchSiteRunSnapshot> ReadBatchHistoryResults(string payloadJson)
    {
        if (!HistoryPayloadReader.TryParse(payloadJson, out var document))
        {
            return [];
        }

        using (document)
        {
            var sites = HistoryPayloadReader.ReadArray(document.RootElement, "sites");
            return sites
                .Select(static site =>
                {
                    var name = HistoryPayloadReader.FirstString(site, ["Name"], ["name"]) ?? "0";
                    var latency = HistoryPayloadReader.FirstDouble(site, ["LatencyMs"], ["latencyMs"]) ?? 0;
                    var ttft = HistoryPayloadReader.FirstDouble(site, ["TtftMs"], ["ttftMs"]);
                    var throughput = HistoryPayloadReader.FirstDouble(site, ["Throughput"], ["throughput"]) ?? 0;
                    var successRate = HistoryPayloadReader.FirstDouble(site, ["SuccessRate"], ["successRate"]) ?? 0;
                    var score = HistoryPayloadReader.FirstDouble(site, ["Score"], ["score"]) ?? 0;
                    var passCount = (int)(HistoryPayloadReader.FirstDouble(site, ["PassCount"], ["passCount"]) ?? 0);
                    var totalCount = (int)(HistoryPayloadReader.FirstDouble(site, ["TotalCount"], ["totalCount"]) ?? 0);
                    var capability = HistoryPayloadReader.FirstString(site, ["CapabilitySummary"], ["capabilitySummary"]) ??
                                     (totalCount > 0 ? $"{Math.Clamp(passCount, 0, totalCount)}/{totalCount}" : "--");
                    var cache = HistoryPayloadReader.FirstString(site, ["CacheState"], ["cacheState"]) ?? "--";
                    var protocol = HistoryPayloadReader.FirstString(site, ["ProtocolSummary"], ["protocolSummary"]) ?? "未探测";
                    var latest = HistoryPayloadReader.FirstString(site, ["LatestResult"], ["latestResult"]) ?? "--";
                    var runCount = (int)(HistoryPayloadReader.FirstDouble(site, ["RunCount"], ["runCount"]) ?? 1);
                    var verdict = HistoryPayloadReader.FirstString(
                            site,
                            ["VerdictText"],
                            ["verdictText"],
                            ["Verdict"],
                            ["verdict"],
                            ["QuickVerdict"],
                            ["quickVerdict"]) ??
                        ResolveBatchSnapshotVerdict(score, successRate, passCount, totalCount, latest);
                    var secondary = HistoryPayloadReader.FirstString(site, ["SecondaryText"], ["secondaryText"]) ??
                                    BuildBatchSnapshotSecondary(latency, ttft, throughput, successRate, score, capability, cache, protocol);
                    return new BatchSiteRunSnapshot(
                        name,
                        latency,
                        ttft,
                        throughput,
                        successRate,
                        score,
                        passCount,
                        totalCount,
                        capability,
                        cache,
                        protocol,
                        latest,
                        verdict,
                        secondary,
                        Math.Max(runCount, 0));
                })
                .ToList();
        }
    }

    private static string ResolveBatchSnapshotVerdict(
        double score,
        double successRate,
        int passCount,
        int totalCount,
        string? latestResult)
    {
        if (!string.IsNullOrWhiteSpace(latestResult) &&
            latestResult.Contains("失败", StringComparison.Ordinal))
        {
            return "待复核";
        }

        var passRatio = totalCount <= 0
            ? Math.Clamp(successRate, 0, 100)
            : Math.Clamp(passCount * 100d / totalCount, 0, 100);

        if (score >= 85 && passRatio >= 90 && successRate >= 95)
        {
            return "稳定";
        }

        if (score >= 60 && passRatio >= 60 && successRate >= 60)
        {
            return "可用";
        }

        return "待复核";
    }

    private static string BuildBatchSnapshotSecondary(
        double latencyMs,
        double? ttftMs,
        double throughput,
        double successRate,
        double score,
        string capability,
        string cache,
        string protocol)
        => $"能力 {NormalizeRankingText(capability)} | 缓存 {NormalizeRankingText(cache)} | 协议 {NormalizeRankingText(protocol)} | 延迟 {latencyMs:F0} ms | TTFT {FormatOptionalMilliseconds(ttftMs)} | 吞吐 {throughput:F1} tok/s | 成功 {successRate:F1}% | 综合 {score:F1}";

    private static string NormalizeRankingText(string? value)
        => string.IsNullOrWhiteSpace(value) ? "--" : value.Trim();

    private static string FormatOptionalMilliseconds(double? value)
        => value.HasValue ? $"{value.Value:F0} ms" : "--";

    private static IReadOnlyList<BatchCompositeTrendSnapshot> BuildHistoricalBatchTrend(
        IReadOnlyList<HistoryReportSummary> summaries,
        IReadOnlyDictionary<string, HistoryReport> reports)
    {
        var trend = new List<BatchCompositeTrendSnapshot>(summaries.Count);
        foreach (var summary in summaries.OrderBy(static item => item.CreatedAtUtc))
        {
            reports.TryGetValue(summary.RunId, out var report);
            trend.Add(ReadBatchTrendSnapshot(summary, report));
        }

        return trend.Count == 0 ? [BatchCompositeTrendSnapshot.Zero] : trend;
    }

    private static BatchCompositeTrendSnapshot ReadBatchTrendSnapshot(
        HistoryReportSummary summary,
        HistoryReport? report)
    {
        if (report is not null)
        {
            if (TryReadBatchAggregateTrend(report.PayloadJson, out var aggregate))
            {
                return aggregate;
            }

            var results = ReadBatchHistoryResults(report.PayloadJson);
            if (results.Count > 0)
            {
                return BuildAggregateBatchTrend(results, summary.Score);
            }
        }

        return new BatchCompositeTrendSnapshot(Math.Clamp(summary.Score ?? 0, 0, 100), 0, 0);
    }

    private static bool TryReadBatchAggregateTrend(
        string? payloadJson,
        out BatchCompositeTrendSnapshot trend)
    {
        trend = BatchCompositeTrendSnapshot.Zero;
        if (!HistoryPayloadReader.TryParse(payloadJson, out var document))
        {
            return false;
        }

        using (document)
        {
            if (!HistoryPayloadReader.TryGetProperty(document.RootElement, "aggregate", out var aggregate))
            {
                return false;
            }

            var score = HistoryPayloadReader.FirstDouble(
                aggregate,
                ["AverageScore"],
                ["averageScore"],
                ["Score"],
                ["score"]);
            var successRate = HistoryPayloadReader.FirstDouble(
                aggregate,
                ["AverageSuccessRate"],
                ["averageSuccessRate"],
                ["SuccessRate"],
                ["successRate"]);
            var throughput = HistoryPayloadReader.FirstDouble(
                aggregate,
                ["AverageThroughput"],
                ["averageThroughput"],
                ["Throughput"],
                ["throughput"]);

            if (!score.HasValue && !successRate.HasValue && !throughput.HasValue)
            {
                return false;
            }

            trend = new BatchCompositeTrendSnapshot(
                Math.Clamp(score ?? 0, 0, 100),
                Math.Clamp(successRate ?? 0, 0, 100),
                Math.Max(0, throughput ?? 0));
            return true;
        }
    }

    private static BatchCompositeTrendSnapshot BuildAggregateBatchTrend(
        IReadOnlyList<BatchSiteRunSnapshot> results,
        double? fallbackScore = null)
    {
        if (results.Count == 0)
        {
            return new BatchCompositeTrendSnapshot(Math.Clamp(fallbackScore ?? 0, 0, 100), 0, 0);
        }

        return new BatchCompositeTrendSnapshot(
            Math.Clamp(results.Average(static item => item.Score), 0, 100),
            Math.Clamp(results.Average(static item => item.SuccessRate), 0, 100),
            Math.Max(0, results.Average(static item => item.Throughput)));
    }

    private static async Task<IReadOnlyList<EndpointHistoryItem>> LoadDefaultEndpointHistoryAsync(CancellationToken ct)
        => await new EndpointHistoryStore().LoadAsync(ct).ConfigureAwait(false);

}
