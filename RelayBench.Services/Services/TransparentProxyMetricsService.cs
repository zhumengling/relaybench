namespace RelayBench.Services;

internal sealed class TransparentProxyMetricsService
{
    private readonly object _syncRoot = new();
    private readonly List<long> _latencies = [];

    public void Reset()
    {
        lock (_syncRoot)
        {
            _latencies.Clear();
        }
    }

    public void TrackLatency(long latencyMs)
    {
        lock (_syncRoot)
        {
            _latencies.Add(latencyMs);
            if (_latencies.Count > 300)
            {
                _latencies.RemoveRange(0, _latencies.Count - 300);
            }
        }
    }

    public TransparentProxyMetricsSnapshot CreateSnapshot(
        bool isRunning,
        TransparentProxyServerConfig? config,
        TransparentProxyMetricsCounters counters,
        IReadOnlyList<TransparentProxyRouteMetrics> routes,
        TransparentProxyTokenTelemetrySnapshot tokenSnapshot,
        TransparentProxyCacheStats cacheStats,
        TransparentProxyPromptSessionCacheStats promptSessionStats,
        IReadOnlyList<TransparentProxyModelPoolSnapshot> modelPools,
        IReadOnlyList<TransparentProxyUsageEvent> usageEvents,
        IReadOnlyList<TransparentProxyIngressMetricsSnapshot> ingressMetrics)
    {
        long p50;
        long p95;
        lock (_syncRoot)
        {
            var orderedLatencies = _latencies.OrderBy(static item => item).ToArray();
            p50 = Percentile(orderedLatencies, 0.50);
            p95 = Percentile(orderedLatencies, 0.95);
        }

        return new TransparentProxyMetricsSnapshot(
            isRunning,
            config?.Port ?? 0,
            counters.ActiveRequests,
            counters.TotalRequests,
            counters.SuccessRequests,
            counters.FailedRequests,
            counters.FallbackRequests,
            counters.CacheHits,
            counters.RateLimitedRequests,
            p50,
            p95,
            cacheStats.ResponseEntries + cacheStats.ModelListEntries + promptSessionStats.Entries,
            routes,
            tokenSnapshot.TotalOutputTokens,
            tokenSnapshot.TokensPerSecond,
            tokenSnapshot.LastTokenActivityAt,
            tokenSnapshot.PromptCacheTokens,
            cacheStats.ResponseEntries,
            cacheStats.ModelListEntries,
            cacheStats.Hits,
            cacheStats.Misses,
            cacheStats.Stores,
            cacheStats.Evictions,
            promptSessionStats.Entries,
            promptSessionStats.Hits,
            promptSessionStats.Misses,
            modelPools,
            cacheStats.InFlightKeys,
            cacheStats.LeaseWaits,
            usageEvents,
            ingressMetrics,
            tokenSnapshot.TotalInputTokens);
    }

    private static long Percentile(IReadOnlyList<long> orderedValues, double percentile)
    {
        if (orderedValues.Count == 0)
        {
            return 0;
        }

        var index = (int)Math.Ceiling(percentile * orderedValues.Count) - 1;
        index = Math.Clamp(index, 0, orderedValues.Count - 1);
        return orderedValues[index];
    }
}

internal sealed record TransparentProxyMetricsCounters(
    int ActiveRequests,
    int TotalRequests,
    int SuccessRequests,
    int FailedRequests,
    int FallbackRequests,
    int CacheHits,
    int RateLimitedRequests);
