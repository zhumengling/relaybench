namespace RelayBench.WinUI.ViewModels;

/// <summary>
/// Represents a model pool entry showing health, protocol, and performance metrics.
/// Unhealthy entries display cooldown remaining in seconds.
/// </summary>
public sealed record ModelPoolEntry(
    string Name,
    int HealthyCount,
    int TotalCount,
    string ProtocolSummary,
    long TotalRequests,
    double BestLatencyMs,
    bool IsUnhealthy,
    double CooldownRemainingSec,
    int FailedRequests = 0,
    int OpenCircuitMembers = 0,
    string RateLimitDisplay = "0",
    string CacheDisplay = "0.0%",
    string LatestError = "")
{
    /// <summary>Inverse of IsUnhealthy for XAML Visibility binding.</summary>
    public bool IsHealthy => !IsUnhealthy;

    /// <summary>Formatted healthy/total display string.</summary>
    public string HealthDisplay => $"{HealthyCount}/{TotalCount}";

    /// <summary>Formatted best latency display string.</summary>
    public string LatencyDisplay => BestLatencyMs > 0 ? $"{BestLatencyMs:F1} ms" : "0 ms";

    /// <summary>Formatted cooldown display string (seconds).</summary>
    public string CooldownDisplay => IsUnhealthy ? $"{CooldownRemainingSec:F0}s" : "0s";

    public string UsageDisplay => $"{TotalRequests} req \u00b7 \u5931\u8d25 {FailedRequests}";

    public string DetailToolTip
    {
        get
        {
            List<string> lines =
            [
                $"\u6a21\u578b: {Name}",
                $"\u534f\u8bae: {ProtocolSummary}",
                $"\u5065\u5eb7: {HealthDisplay}",
                $"\u6700\u4f73\u5ef6\u8fdf: {LatencyDisplay}",
                $"\u8bf7\u6c42/\u5931\u8d25: {TotalRequests}/{FailedRequests}",
                $"\u65ad\u8def\u6210\u5458: {OpenCircuitMembers}",
                $"\u51b7\u5374: {CooldownDisplay}",
                $"\u9650\u6d41: {RateLimitDisplay}",
                $"\u7f13\u5b58: {CacheDisplay}"
            ];

            if (!string.IsNullOrWhiteSpace(LatestError))
            {
                lines.Add($"\u6700\u8fd1\u5f02\u5e38: {LatestError}");
            }

            return string.Join(Environment.NewLine, lines);
        }
    }
}
