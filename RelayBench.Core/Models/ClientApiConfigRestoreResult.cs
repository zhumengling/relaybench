namespace RelayBench.Core.Models;

public sealed record ClientApiConfigRestoreResult(
    bool Succeeded,
    string Summary,
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<string> BackupFiles,
    string? Error);
