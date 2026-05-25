namespace RelayBench.Core.Models;

public sealed record CodexChatMergeResult(
    bool Succeeded,
    CodexChatMergeTarget Target,
    string Summary,
    int RebuckettedThreadCount,
    int UpdatedSessionFileCount,
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<string> BackupFiles,
    string? Warning = null,
    string? Error = null);
