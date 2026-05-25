using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using Microsoft.UI.Xaml;
using RelayBench.Core.Models;
using RelayBench.Services.Infrastructure;
using RelayBench.Services;
using RelayBench.Core.Services;
using RelayBench.WinUI.Charts;
using RelayBench.WinUI.Storage;
using Windows.ApplicationModel.DataTransfer;

namespace RelayBench.WinUI.ViewModels;

public sealed partial class TransparentProxyViewModel
{    // ─── Phase 23: Trend Charts ───────────────────────────────────────────────────

    /// <summary>
    /// Ring buffer capacity for metrics history (300 data points).
    /// </summary>
    private const int MetricsHistoryCapacity = 300;

    /// <summary>
    /// Minimum interval between chart rebuilds to avoid excessive UI updates.
    /// </summary>
    private static readonly TimeSpan ChartUpdateInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Ring buffer storing recent P50 延迟 values.
    /// </summary>
    private readonly List<double> _p50History = new(MetricsHistoryCapacity);

    /// <summary>
    /// Ring buffer storing recent P95 latency values.
    /// </summary>
    private readonly List<double> _p95History = new(MetricsHistoryCapacity);

    /// <summary>
    /// Ring buffer storing recent P99 latency values (estimated as P95 * 1.3).
    /// </summary>
    private readonly List<double> _p99History = new(MetricsHistoryCapacity);

    /// <summary>
    /// Ring buffer storing recent throughput values (tokens per second).
    /// </summary>
    private readonly List<double> _throughputHistory = new(MetricsHistoryCapacity);

    /// <summary>
    /// Ring buffer storing recent request success-rate samples.
    /// </summary>
    private readonly List<double> _successRateHistory = new(MetricsHistoryCapacity);

    /// <summary>
    /// Timestamp of the last chart rebuild to enforce throttling.
    /// </summary>
    private DateTimeOffset _lastChartUpdate = DateTimeOffset.MinValue;

    private ElementTheme _trendChartTheme = ElementTheme.Dark;

    /// <summary>
    /// Latency trend chart series (P50/P95/P99 lines).
    /// </summary>
    [ObservableProperty] public partial ISeries[] LatencyChartSeries { get; set; } = [];

    /// <summary>
    /// Latency chart Y-axes configuration.
    /// </summary>
    [ObservableProperty] public partial Axis[] LatencyChartYAxes { get; set; } = [];

    /// <summary>
    /// Latency chart X-axes configuration.
    /// </summary>
    [ObservableProperty] public partial Axis[] LatencyChartXAxes { get; set; } = [];

    /// <summary>
    /// Throughput area chart series.
    /// </summary>
    [ObservableProperty] public partial ISeries[] ThroughputChartSeries { get; set; } = [];

    /// <summary>
    /// Throughput chart Y-axes configuration.
    /// </summary>
    [ObservableProperty] public partial Axis[] ThroughputChartYAxes { get; set; } = [];

    /// <summary>
    /// Throughput chart X-axes configuration.
    /// </summary>
    [ObservableProperty] public partial Axis[] ThroughputChartXAxes { get; set; } = [];

    /// <summary>
    /// Appends a metrics snapshot to the ring buffer and rebuilds charts if throttle allows.
    /// </summary>
    private void AppendToMetricsHistory(TransparentProxyMetricsSnapshot metrics)
    {
        // Append to ring buffers, removing oldest if at capacity
        AppendToRingBuffer(_p50History, metrics.P50LatencyMs);
        AppendToRingBuffer(_p95History, metrics.P95LatencyMs);
        // Estimate P99 as P95 * 1.3 (reasonable approximation when P99 is not tracked)
        var estimatedP99 = metrics.P95LatencyMs > 0
            ? (long)(metrics.P95LatencyMs * 1.3)
            : 0L;
        AppendToRingBuffer(_p99History, estimatedP99);
        AppendToRingBuffer(_throughputHistory, metrics.TokensPerSecond);
        AppendToRingBuffer(_successRateHistory, CalculatePercentValue(metrics.SuccessRequests, metrics.TotalRequests));

        // Throttle chart rebuilds to every 2 seconds
        var now = DateTimeOffset.UtcNow;
        if (now - _lastChartUpdate < ChartUpdateInterval) return;
        _lastChartUpdate = now;

        RebuildTrendCharts();
    }

    /// <summary>
    /// Rebuilds both latency and throughput chart series from the ring buffer data.
    /// </summary>
    private void RebuildTrendCharts()
    {
        var theme = ChartPalette.ResolveTheme(_trendChartTheme);

        // Latency chart
        var (latencySeries, _) = LatencyTrendChartBuilder.BuildP50P95P99(
            _p50History, _p95History, _p99History, theme);
        LatencyChartSeries = latencySeries;
        LatencyChartYAxes = LatencyTrendChartBuilder.BuildAxes(theme);
        LatencyChartXAxes = LatencyTrendChartBuilder.BuildXAxes(theme);

        // Throughput chart
        var (throughputSeries, _) = ThroughputAreaChartBuilder.Build(_throughputHistory, theme);
        ThroughputChartSeries = throughputSeries;
        ThroughputChartYAxes = ThroughputAreaChartBuilder.BuildYAxes(theme);
        ThroughputChartXAxes = ThroughputAreaChartBuilder.BuildXAxes(theme);
    }

    public void SetTrendChartTheme(ElementTheme theme)
    {
        var resolvedTheme = ChartPalette.ResolveTheme(theme);
        if (_trendChartTheme == resolvedTheme)
        {
            return;
        }

        _trendChartTheme = resolvedTheme;
        RebuildTrendCharts();
    }

    public TransparentProxyTrendSnapshot CreateTrendSnapshot(ElementTheme theme = ElementTheme.Default)
    {
        theme = ChartPalette.ResolveTheme(theme);
        var p50 = _p50History.ToArray();
        var p95 = _p95History.ToArray();
        var p99 = _p99History.ToArray();
        var throughput = _throughputHistory.ToArray();
        var successRates = _successRateHistory.ToArray();
        var (latencySeries, _) = LatencyTrendChartBuilder.BuildP50P95P99(p50, p95, p99, theme);
        var (throughputSeries, _) = ThroughputAreaChartBuilder.Build(throughput, theme);
        var latencySampleCount = Math.Max(p50.Length, Math.Max(p95.Length, p99.Length));
        var throughputSampleCount = throughput.Length;
        var averageP50 = p50.Length == 0 ? 0 : p50.Average();
        var peakP95 = p95.Length == 0 ? 0 : p95.Max();
        var peakThroughput = throughput.Length == 0 ? 0 : throughput.Max();

        return new TransparentProxyTrendSnapshot(
            latencySeries,
            LatencyTrendChartBuilder.BuildAxes(theme),
            LatencyTrendChartBuilder.BuildXAxes(theme),
            throughputSeries,
            ThroughputAreaChartBuilder.BuildYAxes(theme),
            ThroughputAreaChartBuilder.BuildXAxes(theme),
            latencySampleCount,
            throughputSampleCount,
            FormatMilliseconds(averageP50),
            FormatMilliseconds(peakP95),
            FormatTokenSpeed(peakThroughput),
            TokenSpeed,
            SuccessRate,
            CacheHitRate,
            ActiveConnections.ToString(CultureInfo.InvariantCulture),
            TotalRequests.ToString(CultureInfo.InvariantCulture),
            BuildTrendSnapshotSummary(latencySampleCount, throughputSampleCount, averageP50, peakThroughput),
            BuildTrendComparison(successRates, p50, throughput),
            BuildLatencyPointDetails(p50, p95, p99),
            BuildThroughputPointDetails(throughput),
            RouteQueue.Take(8).ToArray(),
            ModelPool.Take(8).ToArray(),
            RecentActivityEvents.Take(8).ToArray());
    }

    private static IReadOnlyList<TransparentProxyTrendPointDetail> BuildLatencyPointDetails(
        IReadOnlyList<double> p50,
        IReadOnlyList<double> p95,
        IReadOnlyList<double> p99)
    {
        var count = Math.Max(p50.Count, Math.Max(p95.Count, p99.Count));
        if (count == 0)
        {
            return [];
        }

        List<TransparentProxyTrendPointDetail> details = new(count);
        for (var index = 0; index < count; index++)
        {
            var p50Text = FormatMilliseconds(ReadTrendValue(p50, index));
            var p95Text = FormatMilliseconds(ReadTrendValue(p95, index));
            var p99Text = FormatMilliseconds(ReadTrendValue(p99, index));
            details.Add(new TransparentProxyTrendPointDetail(
                index + 1,
                $"延迟样本 {index + 1}",
                $"P50 {p50Text}, P95 {p95Text}, P99 {p99Text}"));
        }

        return details;
    }

    private static IReadOnlyList<TransparentProxyTrendPointDetail> BuildThroughputPointDetails(
        IReadOnlyList<double> throughput)
    {
        if (throughput.Count == 0)
        {
            return [];
        }

        List<TransparentProxyTrendPointDetail> details = new(throughput.Count);
        for (var index = 0; index < throughput.Count; index++)
        {
            var valueText = FormatTokenSpeed(throughput[index]);
            details.Add(new TransparentProxyTrendPointDetail(
                index + 1,
                $"吞吐样本 {index + 1}",
                valueText));
        }

        return details;
    }

    private static double ReadTrendValue(IReadOnlyList<double> values, int index)
        => index >= 0 && index < values.Count ? values[index] : 0;

    private string BuildTrendSnapshotSummary(
        int latencySampleCount,
        int throughputSampleCount,
        double averageP50LatencyMs,
        double peakThroughput)
    {
        if (latencySampleCount == 0 && throughputSampleCount == 0)
        {
            return "等待真实透明代理运行样本。启动代理并发送流量后会填充趋势。";
        }

        return $"运行趋势基于 {latencySampleCount} 个延迟样本和 {throughputSampleCount} 个吞吐样本。 " +
               $"平均 P50 延迟为 {FormatMilliseconds(averageP50LatencyMs)}，峰值吞吐为 {FormatTokenSpeed(peakThroughput)}。";
    }

    internal static TransparentProxyTrendComparison BuildTrendComparison(
        IReadOnlyList<double> successRateSamples,
        IReadOnlyList<double> latencyP50Samples,
        IReadOnlyList<double> throughputSamples)
    {
        var sampleCount = Math.Max(successRateSamples.Count, Math.Max(latencyP50Samples.Count, throughputSamples.Count));
        if (sampleCount <= 1)
        {
            return new TransparentProxyTrendComparison(
                "真实运行样本不足，暂时无法比较趋势变化。",
                "样本不足",
                "样本不足",
                "样本不足",
                "样本不足",
                null,
                null,
                null,
                null,
                null,
                null);
        }

        var (earlierSuccess, laterSuccess) = SplitAverage(successRateSamples);
        var (earlierLatency, laterLatency) = SplitAverage(latencyP50Samples);
        var (earlierThroughput, laterThroughput) = SplitAverage(throughputSamples);
        var successDelta = DescribeTrendDelta(laterSuccess, earlierSuccess, "pct", betterWhenHigher: true);
        var latencyDelta = DescribeTrendDelta(laterLatency, earlierLatency, "ms", betterWhenHigher: false);
        var throughputDelta = DescribeTrendDelta(laterThroughput, earlierThroughput, "tok/s", betterWhenHigher: true);
        var volatility = BuildRuntimeTrendVolatilityLabel(successRateSamples, latencyP50Samples, throughputSamples);

        return new TransparentProxyTrendComparison(
            $"成功率 {successDelta}；延迟 {latencyDelta}；吞吐 {throughputDelta}；波动 {volatility}。",
            successDelta,
            latencyDelta,
            throughputDelta,
            volatility,
            earlierSuccess,
            laterSuccess,
            earlierLatency,
            laterLatency,
            earlierThroughput,
            laterThroughput);
    }

    private static (double? Earlier, double? Later) SplitAverage(IReadOnlyList<double> values)
    {
        var normalized = values
            .Where(static value => value > 0d)
            .ToArray();

        if (normalized.Length <= 1)
        {
            return (null, normalized.Length == 1 ? normalized[0] : null);
        }

        var splitIndex = Math.Max(1, normalized.Length / 2);
        var earlier = normalized.Take(splitIndex).Average();
        var later = normalized.Skip(splitIndex).DefaultIfEmpty(normalized[^1]).Average();
        return (earlier, later);
    }

    private static string DescribeTrendDelta(double? current, double? baseline, string unit, bool betterWhenHigher)
    {
        if (!current.HasValue || !baseline.HasValue)
        {
            return "样本不足";
        }

        var delta = current.Value - baseline.Value;
        var absolute = Math.Abs(delta);
        var threshold = unit switch
        {
            "pct" => 5d,
            "ms" => 120d,
            "tok/s" => 5d,
            _ => 4d,
        };

        if (absolute < threshold)
        {
            return "基本持平";
        }

        var improved = betterWhenHigher ? delta > 0 : delta < 0;
        var direction = improved ? "改善" : "转弱";
        var valueText = unit switch
        {
            "pct" => $"{absolute:F1} 个百分点",
            "ms" => $"{absolute:F0} ms",
            "tok/s" => $"{absolute:F1} tok/s",
            _ => $"{absolute:F0}",
        };

        return $"{direction} {valueText}";
    }

    private static string BuildRuntimeTrendVolatilityLabel(
        IReadOnlyList<double> successRateSamples,
        IReadOnlyList<double> latencyP50Samples,
        IReadOnlyList<double> throughputSamples)
    {
        var successSwing = ComputeRelativeSwing(successRateSamples);
        var latencySwing = ComputeRelativeSwing(latencyP50Samples);
        var throughputSwing = ComputeRelativeSwing(throughputSamples);
        var swing = Math.Max(successSwing, Math.Max(latencySwing, throughputSwing));

        return swing switch
        {
            < 0.18d => "稳定",
            < 0.35d => "小幅波动",
            _ => "波动较大",
        };
    }

    private static double ComputeRelativeSwing(IReadOnlyList<double> samples)
    {
        var values = samples
            .Where(static value => value > 0d)
            .ToArray();

        if (values.Length <= 1)
        {
            return 0d;
        }

        var average = values.Average();
        return average <= 0d ? 0d : (values.Max() - values.Min()) / average;
    }

    private static void AppendToRingBuffer(List<double> buffer, double value)
    {
        if (buffer.Count >= MetricsHistoryCapacity)
            buffer.RemoveAt(0);
        buffer.Add(value);
    }

    // ─── Phase 23.4-23.5: Chart Dialog & Toggle ───────────────────────────────────

    /// <summary>
    /// Whether the chart view is in live mode (true) or static image mode (false).
    /// </summary>
    [ObservableProperty] public partial bool IsLiveChartView { get; set; } = true;

    /// <summary>
    /// Whether the chart dialog is currently open.
    /// </summary>
    [ObservableProperty] public partial bool IsChartDialogOpen { get; set; }

    [RelayCommand]
    private void ToggleChartView()
    {
        IsLiveChartView = !IsLiveChartView;
    }

    [RelayCommand]
    private void OpenChartDialog()
    {
        IsChartDialogOpen = true;
    }

    [RelayCommand]
    private void CloseChartDialog()
    {
        IsChartDialogOpen = false;
    }

    // ─── End Phase 23 ─────────────────────────────────────────────────────────────
}
