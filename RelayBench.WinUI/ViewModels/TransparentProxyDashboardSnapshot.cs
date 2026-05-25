namespace RelayBench.WinUI.ViewModels;

public sealed record TransparentProxyDashboardSnapshot(
    long TotalRequests,
    long SuccessRequests,
    long FailedRequests,
    long FallbackRequests,
    long RateLimitedRequests,
    long CacheHits,
    long TotalInputTokens,
    long TotalOutputTokens,
    long PromptCacheTokens,
    long P50LatencyMs,
    long P95LatencyMs,
    double TokensPerSecond,
    int ActiveConnections,
    int RouteCount,
    int ModelPoolCount);
