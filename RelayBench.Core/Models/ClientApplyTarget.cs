namespace RelayBench.Core.Models;

public sealed record ClientApplyTarget(
    string Id,
    string DisplayName,
    ClientApplyProtocolKind Protocol,
    bool IsInstalled,
    bool IsSelectable,
    bool IsProtocolSupported,
    bool IsDefaultSelected,
    string ConfigSummary,
    string? DisabledReason);
