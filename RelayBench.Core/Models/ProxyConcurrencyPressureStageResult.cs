namespace RelayBench.Core.Models;

public sealed record ProxyConcurrencyPressureStageResult(
    int Concurrency,
    int TotalRequests,
    int SuccessCount,
    int RateLimitedCount,
    int ServerErrorCount,
    int TimeoutCount,
    double? P50ChatLatencyMs,
    double? P95ChatLatencyMs,
    double? P50TtftMs,
    double? P95TtftMs,
    double? AverageTokensPerSecond,
    string Summary);
