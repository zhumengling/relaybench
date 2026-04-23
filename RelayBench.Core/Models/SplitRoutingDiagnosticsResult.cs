namespace RelayBench.Core.Models;

public sealed record SplitRoutingDiagnosticsResult(
    DateTimeOffset CheckedAt,
    IReadOnlyList<string> RequestedHosts,
    IReadOnlyList<SplitRoutingAdapterView> Adapters,
    IReadOnlyList<SplitRoutingExitCheck> ExitChecks,
    IReadOnlyList<SplitRoutingDnsView> DnsViews,
    IReadOnlyList<SplitRoutingReachabilityCheck> ReachabilityChecks,
    bool MultiExitSuspected,
    bool DnsSplitSuspected,
    bool ReachabilityIssuesDetected,
    string Summary,
    string? Error);
