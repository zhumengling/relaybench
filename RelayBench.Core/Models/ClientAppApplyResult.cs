namespace RelayBench.Core.Models;

public sealed record ClientAppApplyResult(
    bool Succeeded,
    string Summary,
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<string> BackupFiles,
    IReadOnlyList<string> AppliedTargets,
    string? Error);
