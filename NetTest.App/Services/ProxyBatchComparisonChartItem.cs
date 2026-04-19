namespace NetTest.App.Services;

public sealed record ProxyBatchComparisonChartItem(
    int Rank,
    string Name,
    string BaseUrl,
    double StabilityRatio,
    string StabilityText,
    double? TtftMs,
    double? ChatLatencyMs,
    string Verdict,
    string SecondaryText,
    int RunCount);
