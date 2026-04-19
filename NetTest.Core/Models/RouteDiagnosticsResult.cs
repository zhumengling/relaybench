namespace NetTest.Core.Models;

public sealed record RouteDiagnosticsResult(
    DateTimeOffset CheckedAt,
    string Target,
    IReadOnlyList<string> ResolvedAddresses,
    int MaxHops,
    int TimeoutMilliseconds,
    int SamplesPerHop,
    bool TraceCompleted,
    int ResponsiveHopCount,
    string Summary,
    string? Error,
    string RawTraceOutput,
    IReadOnlyList<RouteHopResult> Hops,
    string? TraceTarget = null,
    IReadOnlyList<string>? SystemResolvedAddresses = null,
    string? ResolutionSummary = null,
    string? TraceEngine = null,
    string? TraceEngineSummary = null);
