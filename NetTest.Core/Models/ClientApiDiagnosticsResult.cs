namespace NetTest.Core.Models;

public sealed record ClientApiDiagnosticsResult(
    DateTimeOffset CheckedAt,
    IReadOnlyList<ClientApiCheck> Checks,
    int InstalledCount,
    int ConfiguredCount,
    int ReachableCount,
    string Summary,
    string? Error);
