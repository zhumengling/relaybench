namespace RelayBench.WinUI.ViewModels;

/// <summary>
/// Represents a single entry in the route queue table showing pending requests per route.
/// Includes circuit breaker state for inline badge display.
/// </summary>
public sealed record RouteQueueEntry(
    string RouteName,
    int PendingCount,
    int Priority,
    double AgeSeconds,
    CircuitBreakerInfo CircuitBreaker,
    string Protocol = "Unknown",
    string LatencyDisplay = "0 ms",
    string TokenSpeedDisplay = "0 tok/s",
    string HealthDisplay = "0.0%",
    string StatusText = "Ready",
    long SentCount = 0,
    long SuccessCount = 0,
    long FailedCount = 0,
    string CooldownDisplay = "0s",
    string LimitDisplay = "0",
    string CacheDisplay = "0.0%",
    string PolicyDisplay = "\u9ed8\u8ba4",
    string LatestError = "",
    string RouteId = "")
{
    public string TrafficDisplay => $"{SentCount}/{FailedCount}";

    public string ReliabilityDisplay => $"{HealthDisplay} \u00b7 \u5931\u8d25 {FailedCount}";

    public string ThrottleDisplay => $"{CooldownDisplay} \u00b7 \u9650\u6d41 {LimitDisplay}";

    public bool HasRouteId => !string.IsNullOrWhiteSpace(RouteId);

    public string DetailToolTip
    {
        get
        {
            List<string> lines =
            [
                $"\u8def\u7531: {RouteName}",
                $"路由 ID: {NormalizeRouteId(RouteId)}",
                $"\u534f\u8bae: {Protocol}",
                $"\u4f18\u5148\u7ea7: {Priority}",
                $"\u5ef6\u8fdf: {LatencyDisplay}",
                $"Token 速度: {TokenSpeedDisplay}",
                $"\u8bf7\u6c42/\u5931\u8d25: {SentCount}/{FailedCount}",
                $"\u5065\u5eb7\u5ea6: {HealthDisplay}",
                $"\u51b7\u5374/\u9650\u6d41: {CooldownDisplay} / {LimitDisplay}",
                $"\u7f13\u5b58: {CacheDisplay}",
                $"\u7b56\u7565: {PolicyDisplay}",
                $"\u72b6\u6001: {StatusText}"
            ];

            if (!string.IsNullOrWhiteSpace(LatestError))
            {
                lines.Add($"\u6700\u8fd1\u5f02\u5e38: {LatestError}");
            }

            return string.Join(Environment.NewLine, lines);
        }
    }

    /// <summary>
    /// Creates a RouteQueueEntry with a default Closed circuit state.
    /// </summary>
    public RouteQueueEntry(string routeName, int pendingCount, int priority, double ageSeconds)
        : this(routeName, pendingCount, priority, ageSeconds, new CircuitBreakerInfo(CircuitState.Closed, 0))
    {
    }

    private static string NormalizeRouteId(string value)
        => string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();
}
