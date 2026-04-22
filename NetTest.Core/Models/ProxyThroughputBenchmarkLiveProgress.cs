namespace NetTest.Core.Models;

public sealed record ProxyThroughputBenchmarkLiveProgress(
    DateTimeOffset ReportedAt,
    string BaseUrl,
    string Model,
    int RequestedSampleCount,
    int CompletedSampleCount,
    int SuccessfulSampleCount,
    int CurrentSampleIndex,
    int SegmentCount,
    TimeSpan CurrentSampleElapsed,
    int? CurrentOutputTokenCount,
    bool CurrentOutputTokenCountEstimated,
    double? CurrentOutputTokensPerSecond,
    double? CurrentEndToEndTokensPerSecond,
    double? LiveMedianOutputTokensPerSecond,
    double? LiveAverageOutputTokensPerSecond,
    double? LiveMinimumOutputTokensPerSecond,
    double? LiveMaximumOutputTokensPerSecond,
    string Summary,
    string? Preview);
