using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;

namespace RelayBench.WinUI.ViewModels;

public sealed record TransparentProxyTrendSnapshot(
    ISeries[] LatencySeries,
    Axis[] LatencyYAxes,
    Axis[] LatencyXAxes,
    ISeries[] ThroughputSeries,
    Axis[] ThroughputYAxes,
    Axis[] ThroughputXAxes,
    int LatencySampleCount,
    int ThroughputSampleCount,
    string AverageP50LatencyText,
    string PeakP95LatencyText,
    string PeakThroughputText,
    string CurrentTokenSpeedText,
    string SuccessRateText,
    string CacheHitRateText,
    string ActiveConnectionText,
    string TotalRequestText,
    string SummaryText,
    TransparentProxyTrendComparison Comparison,
    IReadOnlyList<TransparentProxyTrendPointDetail> LatencyPointDetails,
    IReadOnlyList<TransparentProxyTrendPointDetail> ThroughputPointDetails,
    IReadOnlyList<RouteQueueEntry> RouteRows,
    IReadOnlyList<ModelPoolEntry> ModelRows,
    IReadOnlyList<TransparentProxyActivityEvent> ActivityRows)
{
    public bool HasLatencySamples => LatencySampleCount > 0;

    public bool HasThroughputSamples => ThroughputSampleCount > 0;

    public bool HasRoutes => RouteRows.Count > 0;

    public bool HasModels => ModelRows.Count > 0;

    public bool HasActivities => ActivityRows.Count > 0;
}

public sealed record TransparentProxyTrendPointDetail(
    int Index,
    string Title,
    string Description);

public sealed record TransparentProxyTrendComparison(
    string Summary,
    string SuccessRateDeltaText,
    string LatencyDeltaText,
    string ThroughputDeltaText,
    string VolatilityText,
    double? EarlierAverageSuccessRate,
    double? LaterAverageSuccessRate,
    double? EarlierAverageLatencyMs,
    double? LaterAverageLatencyMs,
    double? EarlierAverageThroughput,
    double? LaterAverageThroughput)
{
    public bool HasEnoughSamples =>
        (EarlierAverageSuccessRate.HasValue && LaterAverageSuccessRate.HasValue) ||
        (EarlierAverageLatencyMs.HasValue && LaterAverageLatencyMs.HasValue) ||
        (EarlierAverageThroughput.HasValue && LaterAverageThroughput.HasValue);
}
