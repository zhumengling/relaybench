using System.Net.Http.Headers;
using RelayBench.Core.Services;

namespace RelayBench.App.Services;

internal sealed class TransparentProxyRetryOrchestrator
{
    public TimeSpan? ResolveRetryAfter(RetryConditionHeaderValue? retryAfter)
    {
        if (retryAfter is null)
        {
            return null;
        }

        if (retryAfter.Delta is { } delta && delta > TimeSpan.Zero)
        {
            return delta;
        }

        if (retryAfter.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.FromSeconds(1);
        }

        return null;
    }

    public int ResolveRouteRequestRetry(TransparentProxyRoute route, TransparentProxyServerConfig config)
        => route.RequestRetry is >= 0
            ? Math.Clamp(route.RequestRetry.Value, 0, 5)
            : Math.Clamp(config.RequestRetry, 0, 5);

    public bool ShouldRetryStatus(int statusCode)
        => statusCode is 408 or 425 or 429 or 500 or 502 or 503 or 504;

    public bool ShouldTryNextModelCandidate(int statusCode)
        => statusCode is 404 or 429 or 503 || statusCode >= 500;

    public bool HasLaterPreparedModelCandidate(
        IReadOnlyList<TransparentProxyPreparedRequest> preparedRequests,
        int currentIndex,
        string currentModel)
        => preparedRequests
            .Skip(currentIndex + 1)
            .Any(item => !string.Equals(
                item.UpstreamModel?.Trim(),
                currentModel?.Trim(),
                StringComparison.OrdinalIgnoreCase));

    public TimeSpan ResolveRetryDelay(
        TimeSpan? retryAfter,
        int sendAttempt,
        TransparentProxyServerConfig config,
        TransparentProxyRoute route)
    {
        var maxIntervalSeconds = route.MaxRetryIntervalSeconds is > 0
            ? route.MaxRetryIntervalSeconds.Value
            : config.MaxRetryIntervalSeconds;
        var maxInterval = TimeSpan.FromSeconds(Math.Clamp(maxIntervalSeconds, 1, 60));
        if (retryAfter is { } explicitDelay && explicitDelay > TimeSpan.Zero)
        {
            return explicitDelay <= maxInterval ? explicitDelay : maxInterval;
        }

        var backoffMs = Math.Min(maxInterval.TotalMilliseconds, 220d * Math.Pow(2d, Math.Clamp(sendAttempt, 0, 5)));
        var jitterMs = Random.Shared.Next(20, 120);
        return TimeSpan.FromMilliseconds(Math.Min(maxInterval.TotalMilliseconds, backoffMs + jitterMs));
    }

    public bool ShouldFallback(int statusCode)
        => statusCode is 404 or 408 or 409 or 425 or 429 || statusCode >= 500;

    public bool ShouldTryNextWireApi(int statusCode, string wireApi, bool hasNextProtocol)
    {
        if (!hasNextProtocol ||
            string.Equals(wireApi, ProxyWireApiProbeService.ChatCompletionsWireApi, StringComparison.Ordinal) ||
            statusCode is 401 or 403 or 429)
        {
            return false;
        }

        return statusCode is 400 or 404 or 405 or 415;
    }
}
