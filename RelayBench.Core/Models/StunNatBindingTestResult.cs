namespace RelayBench.Core.Models;

public sealed record StunNatBindingTestResult(
    string TestName,
    string RequestTarget,
    string RequestMode,
    bool Success,
    string? LocalEndpoint,
    string? MappedAddress,
    string? ResponseOrigin,
    string? AlternateAddress,
    TimeSpan? RoundTrip,
    string Summary,
    string? Error);
