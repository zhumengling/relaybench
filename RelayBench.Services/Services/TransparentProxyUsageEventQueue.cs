using System.Collections.Concurrent;

namespace RelayBench.Services;

internal sealed class TransparentProxyUsageEventQueue
{
    private const int MaxEvents = 1024;

    private readonly ConcurrentQueue<TransparentProxyUsageEvent> _events = new();
    private long _sequence;

    public event EventHandler<TransparentProxyUsageEvent>? UsageEmitted;

    public void Clear()
    {
        while (_events.TryDequeue(out _))
        {
        }
    }

    public IReadOnlyList<TransparentProxyUsageEvent> Snapshot(int maxEvents = 128)
    {
        var limit = Math.Clamp(maxEvents, 1, MaxEvents);
        return _events
            .Reverse()
            .Take(limit)
            .Reverse()
            .ToArray();
    }

    public void Publish(
        TransparentProxyUsageEventKind kind,
        long outputTokenDelta,
        long totalOutputTokens,
        double tokensPerSecond,
        long promptCacheTokenDelta = 0,
        long totalPromptCacheTokens = 0,
        long inputTokenDelta = 0,
        long totalInputTokens = 0,
        bool estimated = false,
        string modelName = "",
        string routeName = "",
        string wireApi = "",
        string cacheState = "",
        string ingressKind = "",
        string sourceApplication = "",
        string captureMode = "")
    {
        if (outputTokenDelta == 0 &&
            promptCacheTokenDelta == 0 &&
            inputTokenDelta == 0 &&
            kind != TransparentProxyUsageEventKind.Reset)
        {
            return;
        }

        var usageEvent = new TransparentProxyUsageEvent(
            Interlocked.Increment(ref _sequence),
            DateTimeOffset.UtcNow,
            kind,
            outputTokenDelta,
            Math.Max(0, totalOutputTokens),
            Math.Max(0d, tokensPerSecond),
            Math.Max(0, promptCacheTokenDelta),
            Math.Max(0, totalPromptCacheTokens),
            Math.Max(0, inputTokenDelta),
            Math.Max(0, totalInputTokens),
            estimated,
            Normalize(modelName),
            Normalize(routeName),
            Normalize(wireApi),
            Normalize(cacheState),
            Normalize(ingressKind),
            Normalize(sourceApplication),
            Normalize(captureMode));

        _events.Enqueue(usageEvent);
        while (_events.Count > MaxEvents && _events.TryDequeue(out _))
        {
        }

        UsageEmitted?.Invoke(this, usageEvent);
    }

    private static string Normalize(string value)
        => string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();
}

public enum TransparentProxyUsageEventKind
{
    OutputDelta,
    OutputReconciled,
    InputTokens,
    PromptCache,
    Reset
}

public sealed record TransparentProxyUsageEvent(
    long Sequence,
    DateTimeOffset Timestamp,
    TransparentProxyUsageEventKind Kind,
    long OutputTokenDelta,
    long TotalOutputTokens,
    double TokensPerSecond,
    long PromptCacheTokenDelta,
    long TotalPromptCacheTokens,
    long InputTokenDelta,
    long TotalInputTokens,
    bool Estimated,
    string ModelName,
    string RouteName,
    string WireApi,
    string CacheState,
    string IngressKind,
    string SourceApplication,
    string CaptureMode);
