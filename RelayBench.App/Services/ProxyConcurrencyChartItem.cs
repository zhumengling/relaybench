namespace RelayBench.App.Services;

public sealed record ProxyConcurrencyChartItem(
    int Concurrency,
    int TotalRequests,
    int SuccessCount,
    int RateLimitedCount,
    int ServerErrorCount,
    int TimeoutCount,
    double SuccessRate,
    double? P50ChatLatencyMs,
    double? P95TtftMs,
    double? AverageTokensPerSecond,
    string Verdict,
    string Summary,
    bool IsStableLimit,
    bool IsRateLimitStart,
    bool IsHighRisk);
