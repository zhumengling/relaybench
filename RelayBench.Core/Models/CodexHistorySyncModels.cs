namespace RelayBench.Core.Models;

public enum CodexHistorySyncTarget
{
    OfficialOpenAi,
    RelayBenchProvider,
    ExplicitProvider
}

public sealed record CodexProviderCounts(
    IReadOnlyDictionary<string, int> Sessions,
    IReadOnlyDictionary<string, int> ArchivedSessions,
    bool Unreadable = false,
    string? Error = null);

public sealed record CodexSqliteRepairStats(
    int UserEventRowsNeedingRepair,
    int CwdRowsNeedingRepair);

public sealed record CodexProjectThreadVisibility(
    string Root,
    int InteractiveThreads,
    int FirstPageThreads,
    int ExactCwdMatches,
    int VerbatimCwdRows,
    IReadOnlyList<int> Ranks,
    string RankPreview,
    IReadOnlyDictionary<string, int> ProviderCounts);

public sealed record CodexHistoryBackupSummary(
    string BackupRoot,
    int Count,
    long TotalBytes);

public sealed record CodexHistoryBackupPruneResult(
    string BackupRoot,
    int DeletedCount,
    int RemainingCount,
    long FreedBytes);

public sealed record CodexHistorySyncStatus(
    string CodexHome,
    string CurrentProvider,
    bool CurrentProviderImplicit,
    IReadOnlyList<string> ConfiguredProviders,
    CodexProviderCounts RolloutCounts,
    IReadOnlyList<string> LockedRolloutFiles,
    CodexProviderCounts EncryptedContentCounts,
    string? EncryptedContentWarning,
    CodexProviderCounts? SqliteCounts,
    CodexSqliteRepairStats? SqliteRepairStats,
    IReadOnlyList<CodexProjectThreadVisibility> ProjectThreadVisibility,
    CodexHistoryBackupSummary BackupSummary);

public sealed record CodexHistorySyncResult(
    bool Succeeded,
    CodexHistorySyncTarget Target,
    string TargetProvider,
    string CodexHome,
    string Summary,
    int ChangedSessionFileCount,
    IReadOnlyList<string> SkippedLockedRolloutFiles,
    int SqliteRowsUpdated,
    int SqliteProviderRowsUpdated,
    int SqliteUserEventRowsUpdated,
    int SqliteCwdRowsUpdated,
    int UpdatedWorkspaceRoots,
    int SavedWorkspaceRootCount,
    bool SqlitePresent,
    string? BackupDir,
    IReadOnlyList<string> ChangedFiles,
    string? Warning = null,
    string? Error = null);

public sealed record CodexHistoryRestoreOptions(
    bool RestoreConfig = true,
    bool RestoreDatabase = true,
    bool RestoreSessions = true);

public sealed record CodexHistoryRestoreResult(
    bool Succeeded,
    string CodexHome,
    string BackupDir,
    string Summary,
    string? TargetProvider = null,
    DateTimeOffset? CreatedAt = null,
    int ChangedSessionFiles = 0,
    string? Error = null);
