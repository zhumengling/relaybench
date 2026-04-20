namespace NetTest.Core.Models;

public sealed record ClientApiProbeResponse(
    int? StatusCode,
    TimeSpan? Latency,
    string Verdict,
    string? Evidence,
    string? Error);
