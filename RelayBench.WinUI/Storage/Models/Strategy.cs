namespace RelayBench.WinUI.Storage;

/// <summary>
/// Represents a routing strategy with match rules and target routes.
/// </summary>
public sealed record Strategy(
    long Id,
    string Name,
    int Priority,
    string? ModelPattern,
    string? EndpointPattern,
    IReadOnlyList<string> TargetRouteIds,
    DateTime UpdatedAtUtc);
