namespace RelayBench.Core.Models;

public sealed record ClientAppApplyResult(
    bool Succeeded,
    string Summary,
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<string> BackupFiles,
    IReadOnlyList<string> AppliedTargets,
    string? Error)
{
    public IReadOnlyList<ClientAppTargetApplyResult> TargetResults { get; init; } =
        AppliedTargets
            .Select(target => new ClientAppTargetApplyResult(
                target,
                target,
                ClientApplyProtocolKind.Responses,
                Succeeded,
                ChangedFiles,
                BackupFiles,
                Error))
            .ToArray();
}

public sealed record ClientAppTargetApplyResult(
    string TargetId,
    string DisplayName,
    ClientApplyProtocolKind Protocol,
    bool Succeeded,
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<string> BackupFiles,
    string? Error);
