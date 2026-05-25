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
    private static async Task RecordRunHistoryAsync(
        List<BatchSiteRunSnapshot> results,
        TimeSpan elapsed,
        bool cancelled,
        BatchRunMode runMode)
    {
        if (results.Count == 0)
        {
            return;
        }

        try
        {
            var top = results.OrderByDescending(static item => item.Score).First();
            var aggregate = BuildAggregateBatchTrend(results);
            var modeText = runMode == BatchRunMode.Deep ? "深度测试" : "快速测试";
            var payload = System.Text.Json.JsonSerializer.Serialize(new
            {
                cancelled,
                mode = modeText,
                aggregate = new
                {
                    siteCount = results.Count,
                    averageScore = aggregate.Score,
                    averageSuccessRate = aggregate.SuccessRate,
                    averageThroughput = aggregate.Throughput,
                    topSiteName = top.Name,
                    topSiteScore = top.Score
                },
                sites = results.Select(static item => new
                {
                    item.Name,
                    item.LatencyMs,
                    item.TtftMs,
                    item.Throughput,
                    item.SuccessRate,
                    item.Score,
                    item.PassCount,
                    item.TotalCount,
                    item.CapabilitySummary,
                    item.CacheState,
                    item.ProtocolSummary,
                    item.LatestResult,
                    item.VerdictText,
                    item.SecondaryText,
                    item.RunCount
                })
            });

            await RunHistoryRecorder.RecordAsync(
                "批量评测",
                $"{modeText} · {results.Count} 个Site",
                $"{(cancelled ? $"{modeText}已Cancel" : $"{modeText}完成")}。最佳Site: {top.Name} ({top.Score:F1})",
                aggregate.Score,
                (int)elapsed.TotalMilliseconds,
                payload);
        }
        catch
        {
            // History persistence is best-effort.
        }
    }

    private static (int PassCount, int TotalCount, double SuccessRate) ResolveSuccessMetrics(ProxyDiagnosticsResult result)
    {
        if (result.ScenarioResults is { Count: > 0 } scenarios)
        {
            var throughputTotal = result.ThroughputBenchmarkResult is null ? 0 : 1;
            var throughputPass = result.ThroughputBenchmarkResult?.SuccessfulSampleCount > 0 ? 1 : 0;
            var total = scenarios.Count + throughputTotal;
            var pass = scenarios.Count(static scenario => scenario.Success) + throughputPass;
            return (pass, total, total == 0 ? 0 : pass * 100.0 / total);
        }

        var fallbackTotal = 3 + (result.ThroughputBenchmarkResult is null ? 0 : 1);
        var fallbackPass = (result.ChatRequestSucceeded ? 1 : 0) +
                           (result.StreamRequestSucceeded ? 1 : 0) +
                           (result.ModelsRequestSucceeded ? 1 : 0) +
                           (result.ThroughputBenchmarkResult?.SuccessfulSampleCount > 0 ? 1 : 0);
        return (fallbackPass, fallbackTotal, fallbackPass * 100.0 / fallbackTotal);
    }

    private static double ResolveLatencyMs(ProxyDiagnosticsResult result, TimeSpan fallbackElapsed)
    {
        var samples = new List<double>();
        AddMilliseconds(samples, result.ChatLatency);
        AddMilliseconds(samples, result.ModelsLatency);

        if (result.ScenarioResults is not null)
        {
            foreach (var scenario in result.ScenarioResults)
            {
                AddMilliseconds(samples, scenario.Latency ?? scenario.Duration);
            }
        }

        return Median(samples) ?? fallbackElapsed.TotalMilliseconds;
    }

    private static double? ResolveTtftMs(ProxyDiagnosticsResult result)
    {
        var samples = new List<double>();
        AddMilliseconds(samples, result.StreamFirstTokenLatency);

        if (result.ScenarioResults is not null)
        {
            foreach (var scenario in result.ScenarioResults)
            {
                AddMilliseconds(samples, scenario.FirstTokenLatency);
            }
        }

        return Median(samples);
    }

    private static double ResolveThroughput(ProxyDiagnosticsResult result)
    {
        if (result.ThroughputBenchmarkResult?.MedianOutputTokensPerSecond is > 0)
        {
            return result.ThroughputBenchmarkResult.MedianOutputTokensPerSecond.Value;
        }

        if (result.ThroughputBenchmarkResult?.AverageOutputTokensPerSecond is > 0)
        {
            return result.ThroughputBenchmarkResult.AverageOutputTokensPerSecond.Value;
        }

        if (result.ScenarioResults is null)
        {
            return 0;
        }

        var samples = result.ScenarioResults
            .Select(static scenario => scenario.OutputTokensPerSecond ?? scenario.EndToEndTokensPerSecond)
            .Where(static value => value is > 0)
            .Select(static value => value!.Value)
            .ToList();

        return samples.Count == 0 ? 0 : samples.Average();
    }

    private static void AddMilliseconds(List<double> samples, TimeSpan? value)
    {
        if (value is { TotalMilliseconds: > 0 } timeSpan)
        {
            samples.Add(timeSpan.TotalMilliseconds);
        }
    }

    private static double? Median(List<double> values)
    {
        if (values.Count == 0)
        {
            return null;
        }

        values.Sort();
        var middle = values.Count / 2;
        return values.Count % 2 == 1
            ? values[middle]
            : (values[middle - 1] + values[middle]) / 2.0;
    }

    /// <summary>
    /// Computes a composite score from latency, throughput, and success rate.
    /// Score is 0-100 where higher is better.
    /// </summary>
    public static double ComputeCompositeScore(double latencyMs, double throughput, double successRate)
    {
        var normalizedLatencyMs = Math.Clamp(latencyMs, 0.0, 5000.0);
        var normalizedThroughput = Math.Max(0.0, throughput);
        var normalizedSuccessRate = Math.Clamp(successRate, 0.0, 100.0);

        // Latency component: lower is better, normalize to 0-40 range (0ms=40, 5000ms=0).
        double latencyScore = Math.Max(0, 40.0 * (1.0 - normalizedLatencyMs / 5000.0));

        // Throughput component: higher is better, normalize to 0-30 range.
        double throughputScore = Math.Min(30.0, normalizedThroughput / 10.0 * 30.0);

        // Success component: 0-30 range.
        double successScore = normalizedSuccessRate / 100.0 * 30.0;

        return Math.Clamp(Math.Round(latencyScore + throughputScore + successScore, 1), 0.0, 100.0);
    }

    /// <summary>
    /// Updates the SiteRankings collection with real batch results.
    /// </summary>
    private void UpdateRankings(List<BatchSiteRunSnapshot> results)
    {
        var sorted = results.OrderByDescending(r => r.Score).ToList();
        SiteRankings.Clear();
        for (int i = 0; i < sorted.Count; i++)
        {
            var r = sorted[i];
            var source = ResolveRankingSourceByName(r.Name);
            SiteRankings.Add(new SiteRankEntry(i + 1, r.Name, r.LatencyMs, r.TtftMs, r.Throughput, r.SuccessRate, r.Score)
            {
                BaseUrl = source?.BaseUrl ?? string.Empty,
                ApiKey = source?.ApiKey ?? string.Empty,
                Model = source?.Model ?? string.Empty,
                CapabilityText = string.IsNullOrWhiteSpace(r.CapabilitySummary) ||
                                 string.Equals(r.CapabilitySummary, "--", StringComparison.Ordinal)
                    ? r.CapabilityText
                    : r.CapabilitySummary,
                CacheStateText = string.IsNullOrWhiteSpace(r.CacheState) ? "--" : r.CacheState,
                ProtocolSummary = string.IsNullOrWhiteSpace(r.ProtocolSummary) ? "未探测" : r.ProtocolSummary,
                LatestResult = string.IsNullOrWhiteSpace(r.LatestResult) ? "--" : r.LatestResult,
                VerdictText = string.IsNullOrWhiteSpace(r.VerdictText) ? "--" : r.VerdictText,
                SecondaryText = string.IsNullOrWhiteSpace(r.SecondaryText) ? "--" : r.SecondaryText,
                RunCount = r.RunCount
            });
        }
    }

    private void UpdateRealtimeBatchResults(
        List<BatchSiteRunSnapshot> results,
        bool useHistoricalCompositeTrend = false)
    {
        UpdateRankings(results);
        BuildComparisonCharts(results);
        if (useHistoricalCompositeTrend)
        {
            BuildCompositeTrend(_historicalBatchTrend);
        }
        else
        {
            BuildCompositePolyline(results);
        }

        BuildHeatmap(results);
    }

    private void ResetRealtimeBatchVisuals()
    {
        SiteRankings.Clear();
        BuildComparisonCharts([]);
        BuildCompositeTrend([BatchCompositeTrendSnapshot.Zero]);
        if (HeatmapCellTones is not null)
        {
            HeatmapCellTones = CreateDefaultHeatmapTones();
        }

        HasHeatmapData = false;
        OnPropertyChanged(nameof(HasHeatmapData));
    }

    private void BuildCompositePolyline(List<BatchSiteRunSnapshot> results)
    {
        BatchCompositeTrendSnapshot[] trend = results.Count == 0
            ? [BatchCompositeTrendSnapshot.Zero]
            : results
                .Select(static item => new BatchCompositeTrendSnapshot(item.Score, item.SuccessRate, item.Throughput))
                .ToArray();

        BuildCompositeTrend(trend);
    }

    private void BuildCompositeTrend(IReadOnlyList<BatchCompositeTrendSnapshot> trend)
    {
        BatchCompositeTrendSnapshot[] normalized = trend.Count == 0
            ? [BatchCompositeTrendSnapshot.Zero]
            : trend.TakeLast(14).ToArray();

        if (normalized.Length == 1)
        {
            normalized = [normalized[0], normalized[0]];
        }

        const double width = 240;
        const double height = 120;
        var maxScore = Math.Max(100d, normalized.Max(static item => item.Score));
        var scorePoints = new List<Windows.Foundation.Point>();
        var successPoints = new List<Windows.Foundation.Point>();
        var throughputPoints = new List<Windows.Foundation.Point>();
        var maxThroughput = Math.Max(1d, normalized.Max(static item => item.Throughput));

        for (var index = 0; index < normalized.Length; index++)
        {
            var x = normalized.Length == 1 ? width / 2 : index * width / (normalized.Length - 1);
            var score = Math.Clamp(normalized[index].Score, 0, maxScore);
            var successRate = Math.Clamp(normalized[index].SuccessRate, 0, 100);
            var throughput = Math.Clamp(normalized[index].Throughput, 0, maxThroughput);
            scorePoints.Add(new Windows.Foundation.Point(x, height - (score / maxScore * height)));
            successPoints.Add(new Windows.Foundation.Point(x, height - (successRate / 100d * height)));
            throughputPoints.Add(new Windows.Foundation.Point(x, height - (throughput / maxThroughput * height)));
        }

        TrySetCompositePolylines(scorePoints, successPoints, throughputPoints);
    }

    private void TrySetCompositePolylines(
        IReadOnlyList<Windows.Foundation.Point> scorePoints,
        IReadOnlyList<Windows.Foundation.Point> successPoints,
        IReadOnlyList<Windows.Foundation.Point> throughputPoints)
    {
        try
        {
            CompositePolyline1Points = BuildPointCollection(scorePoints);
            CompositePolyline2Points = BuildPointCollection(successPoints);
            CompositePolyline3Points = BuildPointCollection(throughputPoints);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Unit tests can run outside the WinUI runtime.
        }
    }

    private static PointCollection BuildPointCollection(IReadOnlyList<Windows.Foundation.Point> points)
    {
        var collection = new PointCollection();
        foreach (var point in points)
        {
            collection.Add(point);
        }

        return collection;
    }

    private void BuildHeatmap(List<BatchSiteRunSnapshot> results)
    {
        if (HeatmapCellTones is null)
        {
            return;
        }

        var cells = new List<BatchHeatmapCellTone>(36);
        for (var index = 0; index < 36; index++)
        {
            if (index >= results.Count)
            {
                cells.Add(BatchHeatmapCellTone.Empty);
                continue;
            }

            cells.Add(ResolveHeatmapTone(results[index].Score));
        }

        HeatmapCellTones = new ObservableCollection<BatchHeatmapCellTone>(cells);
        HasHeatmapData = results.Count > 0;
        OnPropertyChanged(nameof(HasHeatmapData));
    }

    private static ObservableCollection<BatchHeatmapCellTone> CreateDefaultHeatmapTones()
        => new(Enumerable.Repeat(BatchHeatmapCellTone.Empty, 36));

    private static BatchHeatmapCellTone ResolveHeatmapTone(double score)
        => score >= 85
            ? BatchHeatmapCellTone.Healthy
            : score >= 60
                ? BatchHeatmapCellTone.Warning
                : BatchHeatmapCellTone.Danger;

    /// <summary>
    /// Builds comparison charts (box plot for latency, grouped bar for throughput) from batch results.
    /// </summary>
    private void BuildComparisonCharts(List<BatchSiteRunSnapshot> results)
    {
        var theme = ElementTheme.Default;
        var chartResults = results.Count == 0
            ? new List<BatchSiteRunSnapshot> { BuildZeroBatchSiteSnapshot() }
            : results;

        // Build latency box plot (using P50 column chart as fallback)
        var latencySites = chartResults.Select(r => (r.Name, (IReadOnlyList<double>)new List<double> { r.LatencyMs })).ToList();
        var (latSeries, latXAxes, latEmpty) = BoxPlotChartBuilder.Build(latencySites, theme);
        if (!latEmpty)
        {
            LatencyChartSeries = latSeries;
            LatencyChartXAxes = latXAxes;
            LatencyChartYAxes = BoxPlotChartBuilder.BuildYAxes(theme);
            HasLatencyChart = true;
        }
        else
        {
            LatencyChartSeries = [];
            LatencyChartXAxes = [];
            LatencyChartYAxes = [];
            HasLatencyChart = false;
        }

        // Build throughput grouped bar chart
        var throughputSites = chartResults.Select(r => (r.Name, r.Throughput)).ToList();
        var (tpSeries, tpXAxes, tpEmpty) = GroupedBarChartBuilder.Build(throughputSites, theme);
        if (!tpEmpty)
        {
            ThroughputChartSeries = tpSeries;
            ThroughputChartXAxes = tpXAxes;
            ThroughputChartYAxes = GroupedBarChartBuilder.BuildYAxes(theme);
            HasThroughputChart = true;
        }
        else
        {
            ThroughputChartSeries = [];
            ThroughputChartXAxes = [];
            ThroughputChartYAxes = [];
            HasThroughputChart = false;
        }
    }

    private static BatchSiteRunSnapshot BuildZeroBatchSiteSnapshot()
        => new("0", 0, null, 0, 0, 0);

    private sealed record BatchCompositeTrendSnapshot(double Score, double SuccessRate, double Throughput)
    {
        public static readonly BatchCompositeTrendSnapshot Zero = new(0, 0, 0);
    }

    private sealed record BatchSiteRunResult(
        string Name,
        double LatencyMs,
        double? TtftMs,
        double Throughput,
        double SuccessRate,
        double Score,
        int PassCount,
        int TotalCount,
        TimeSpan Elapsed);

    [RelayCommand]
    private void StopBatch()
    {
        _cts?.Cancel();
        IsRunning = false;
        StatusText = "批量评测已停止";
    }
}
