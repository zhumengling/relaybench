namespace NetTest.Core.Models;

public sealed record SplitRoutingExitCheck(
    string Name,
    string Endpoint,
    bool Succeeded,
    string? PublicIp,
    string? LocationCode,
    string? CloudflareColo,
    string? Country,
    string? City,
    TimeSpan? Latency,
    string Summary,
    string? Error);
