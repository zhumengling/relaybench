namespace RelayBench.Core.Models;

public sealed record SplitRoutingDnsView(
    string Host,
    IReadOnlyList<string> SystemAddresses,
    IReadOnlyList<string> CloudflareAddresses,
    IReadOnlyList<string> GoogleAddresses,
    TimeSpan? SystemLatency,
    TimeSpan? CloudflareLatency,
    TimeSpan? GoogleLatency,
    string ComparisonSummary,
    string? Error);
