namespace NetTest.Core.Models;

public sealed record UnlockEndpointCheck(
    string Name,
    string Provider,
    string Url,
    string Method,
    bool Reachable,
    int? StatusCode,
    TimeSpan? Latency,
    string Verdict,
    string SemanticCategory,
    string SemanticVerdict,
    string Summary,
    string SemanticSummary,
    string? Evidence,
    string? FinalUrl,
    string? ResponseContentType,
    string? Error);
