namespace NetTest.Core.Models;

public sealed record SplitRoutingReachabilityCheck(
    string Host,
    string Url,
    bool Succeeded,
    int? StatusCode,
    string? ResolvedAddress,
    TimeSpan? Latency,
    string Summary,
    string? Error);
