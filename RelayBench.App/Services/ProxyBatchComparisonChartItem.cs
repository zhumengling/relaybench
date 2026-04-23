namespace RelayBench.App.Services;

public sealed record ProxyBatchComparisonChartItem(
    int Rank,
    string Name,
    string BaseUrl,
    double CompositeScore,
    string CompositeText,
    double StabilityRatio,
    string StabilityText,
    double? TtftMs,
    double? ChatLatencyMs,
    double? TokensPerSecond,
    string Verdict,
    string SecondaryText,
    int RunCount);
