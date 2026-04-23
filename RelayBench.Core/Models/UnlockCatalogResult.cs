namespace RelayBench.Core.Models;

public sealed record UnlockCatalogResult(
    DateTimeOffset CheckedAt,
    IReadOnlyList<UnlockEndpointCheck> Checks,
    int ReachableCount,
    int SemanticReadyCount,
    int AuthenticationRequiredCount,
    int RegionRestrictedCount,
    int ReviewRequiredCount,
    string Summary,
    string? Error);
