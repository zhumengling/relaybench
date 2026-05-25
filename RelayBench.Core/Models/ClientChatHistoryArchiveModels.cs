namespace RelayBench.Core.Models;

public sealed record ClientChatHistoryArchiveResult(
    bool Succeeded,
    string Summary,
    string ArchivePath,
    int CodexFileCount,
    int ClaudeFileCount,
    string? BackupDirectory = null,
    string? Error = null);

public sealed record ClientChatHistoryArchiveManifest(
    string Schema,
    int Version,
    DateTimeOffset ExportedAtUtc,
    IReadOnlyList<ClientChatHistoryArchiveEntry> Entries);

public sealed record ClientChatHistoryArchiveEntry(
    string Client,
    string RelativePath,
    string ArchivePath,
    long Length,
    string Sha256);
