using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using RelayBench.Core.Models;

namespace RelayBench.Core.Services;

public sealed partial class CodexHistorySyncService
{
    public const string OfficialOpenAiProvider = "openai";
    public const string RelayBenchProvider = "relaybench";

    private const string BackupNamespace = "provider-sync";
    private const string DbFileBasename = "state_5.sqlite";
    private const string GlobalStateFileBasename = ".codex-global-state.json";
    private const string GlobalStateBackupFileBasename = ".codex-global-state.json.bak";
    private const int DefaultBackupRetentionCount = 5;
    private const int DefaultSqliteBusyTimeoutMs = 5000;
    private const int ScanBufferSize = 1024 * 1024;
    private const string StatusOnlyProvider = "__status_only__";

    private static readonly string[] SessionDirectories = ["sessions", "archived_sessions"];

    private readonly IClientApiConfigMutationEnvironment _environment;

    static CodexHistorySyncService()
    {
        SQLitePCL.Batteries_V2.Init();
    }

    public CodexHistorySyncService(IClientApiConfigMutationEnvironment? environment = null)
    {
        _environment = environment ?? new ClientApiDiagnosticEnvironment();
    }

    public async Task<CodexHistorySyncStatus> GetStatusAsync(string? explicitCodexHome = null)
    {
        var codexHome = NormalizeCodexHome(explicitCodexHome);
        if (!Directory.Exists(codexHome))
        {
            return new CodexHistorySyncStatus(
                codexHome,
                OfficialOpenAiProvider,
                true,
                [OfficialOpenAiProvider],
                EmptyProviderCounts(),
                [],
                EmptyProviderCounts(),
                null,
                null,
                null,
                [],
                await GetBackupSummaryAsync(codexHome));
        }

        var configPath = ConfigPath(codexHome);
        var configText = File.Exists(configPath) ? await File.ReadAllTextAsync(configPath) : string.Empty;
        var currentProvider = ReadCurrentProviderFromConfigText(configText);
        var sessionInfo = await CollectSessionChangesAsync(codexHome, StatusOnlyProvider, skipLockedReads: true);
        var sqliteCounts = await ReadSqliteProviderCountsAsync(codexHome);
        var sqliteRepairStats = sqliteCounts is not null && !sqliteCounts.Unreadable
            ? await ReadSqliteRepairStatsAsync(codexHome, sessionInfo.UserEventThreadIds, sessionInfo.ThreadCwdsById)
            : null;
        var projectVisibility = sqliteCounts?.Unreadable == true
            ? []
            : await ReadProjectThreadVisibilityAsync(codexHome);

        return new CodexHistorySyncStatus(
            codexHome,
            currentProvider.Provider,
            currentProvider.Implicit,
            ListConfiguredProviderIds(configText),
            sessionInfo.ProviderCounts,
            sessionInfo.LockedPaths,
            sessionInfo.EncryptedContentCounts,
            BuildEncryptedContentWarning(sessionInfo.EncryptedContentCounts, currentProvider.Provider),
            sqliteCounts,
            sqliteRepairStats,
            projectVisibility,
            await GetBackupSummaryAsync(codexHome));
    }

    public async Task<CodexHistorySyncResult> SyncAsync(
        CodexHistorySyncTarget target,
        string? explicitProvider = null,
        string? explicitCodexHome = null,
        int keepCount = DefaultBackupRetentionCount,
        int? sqliteBusyTimeoutMs = null)
    {
        var codexHome = NormalizeCodexHome(explicitCodexHome);
        var targetProvider = ResolveTargetProvider(target, explicitProvider);
        if (!Directory.Exists(codexHome))
        {
            return BuildFailedResult(
                target,
                targetProvider,
                codexHome,
                "未发现 .codex 目录，暂时无法同步 Codex 历史记录。",
                "missing-codex-root");
        }

        try
        {
            await using var lockHandle = await AcquireLockAsync(codexHome, "history-sync");
            var configPath = ConfigPath(codexHome);
            var originalConfigText = File.Exists(configPath) ? await File.ReadAllTextAsync(configPath) : string.Empty;
            var sessionInfo = await CollectSessionChangesAsync(codexHome, targetProvider, skipLockedReads: true);
            var (writableChanges, lockedChanges) = await SplitLockedSessionChangesAsync(sessionInfo.Changes);
            var skippedLockedFiles = sessionInfo.LockedPaths
                .Concat(lockedChanges.Select(static change => change.Path))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList();

            await AssertSqliteWritableAsync(codexHome, sqliteBusyTimeoutMs);

            var workspaceCwdStats = await ReadThreadCwdStatsAsync(codexHome);
            var backupDir = await CreateBackupAsync(codexHome, targetProvider, writableChanges, originalConfigText);
            var changedFiles = new List<string>();
            var appliedSessionChanges = new List<SessionChange>();
            var globalStateRestoreNeeded = false;
            var configRestoreNeeded = false;

            try
            {
                if (WriteConfigProvider(configPath, originalConfigText, targetProvider))
                {
                    changedFiles.Add(configPath);
                    configRestoreNeeded = true;
                }

                var workspaceRootResult = new WorkspaceRootSyncResult(false, false, 0, 0);
                SessionApplyResult applyResult = new(0, [], []);
                var sqliteResult = await UpdateSqliteProviderAsync(
                    codexHome,
                    targetProvider,
                    async () =>
                    {
                        if (writableChanges.Count > 0)
                        {
                            applyResult = await ApplySessionChangesAsync(writableChanges);
                            var appliedPathSet = new HashSet<string>(applyResult.AppliedPaths, StringComparer.Ordinal);
                            appliedSessionChanges = writableChanges
                                .Where(change => appliedPathSet.Contains(change.Path))
                                .ToList();
                            await UpdateSessionBackupManifestAsync(backupDir, appliedSessionChanges);
                            changedFiles.AddRange(applyResult.AppliedPaths);
                        }

                        workspaceRootResult = await SyncWorkspaceRootsAsync(codexHome, workspaceCwdStats);
                        if (workspaceRootResult.Updated)
                        {
                            changedFiles.Add(GlobalStatePath(codexHome));
                            globalStateRestoreNeeded = true;
                        }
                    },
                    sqliteBusyTimeoutMs,
                    sessionInfo.UserEventThreadIds,
                    sessionInfo.ThreadCwdsById);

                skippedLockedFiles = skippedLockedFiles
                    .Concat(applyResult.SkippedPaths)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToList();

                var pruneWarning = string.Empty;
                try
                {
                    await PruneBackupsAsync(codexHome, keepCount);
                }
                catch (Exception error)
                {
                    pruneWarning = $"备份清理失败：{error.Message}";
                }

                var encryptedWarning = BuildEncryptedContentWarning(sessionInfo.EncryptedContentCounts, targetProvider);
                var warning = string.Join(
                    Environment.NewLine,
                    new[] { encryptedWarning, pruneWarning }
                        .Where(static item => !string.IsNullOrWhiteSpace(item)));

                return new CodexHistorySyncResult(
                    true,
                    target,
                    targetProvider,
                    codexHome,
                    $"已将 Codex 历史同步到“{BuildProviderDisplayName(targetProvider)}”：SQLite 更新 {sqliteResult.UpdatedRows} 行，记录文件更新 {applyResult.AppliedCount} 份。",
                    applyResult.AppliedCount,
                    skippedLockedFiles,
                    sqliteResult.UpdatedRows,
                    sqliteResult.ProviderRowsUpdated,
                    sqliteResult.UserEventRowsUpdated,
                    sqliteResult.CwdRowsUpdated,
                    workspaceRootResult.UpdatedWorkspaceRoots,
                    workspaceRootResult.SavedWorkspaceRootCount,
                    sqliteResult.DatabasePresent,
                    backupDir,
                    changedFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                    string.IsNullOrWhiteSpace(warning) ? null : warning,
                    null);
            }
            catch
            {
                if (appliedSessionChanges.Count > 0)
                {
                    await RestoreSessionChangesAsync(appliedSessionChanges);
                }

                if (globalStateRestoreNeeded)
                {
                    await RestoreGlobalStateFilesAsync(backupDir, codexHome);
                }

                if (configRestoreNeeded)
                {
                    await File.WriteAllTextAsync(configPath, originalConfigText);
                }

                throw;
            }
        }
        catch (Exception error)
        {
            return BuildFailedResult(
                target,
                targetProvider,
                codexHome,
                "Codex 历史同步失败。",
                error.Message);
        }
    }

    public async Task<CodexHistoryRestoreResult> RestoreAsync(
        string backupDir,
        CodexHistoryRestoreOptions? options = null,
        string? explicitCodexHome = null)
    {
        options ??= new CodexHistoryRestoreOptions();
        var codexHome = NormalizeCodexHome(explicitCodexHome);
        try
        {
            await using var lockHandle = await AcquireLockAsync(codexHome, "history-restore");
            var normalizedBackupDir = Path.GetFullPath(backupDir);
            var metadata = await ReadBackupMetadataAsync(normalizedBackupDir);
            if (!string.Equals(metadata.CodexHome, codexHome, StringComparison.OrdinalIgnoreCase))
            {
                return new CodexHistoryRestoreResult(
                    false,
                    codexHome,
                    normalizedBackupDir,
                    $"备份属于 {metadata.CodexHome}，不是当前 Codex 目录。",
                    metadata.TargetProvider,
                    metadata.CreatedAt,
                    metadata.ChangedSessionFiles,
                    "backup-codex-home-mismatch");
            }

            SessionBackupManifest? sessionManifest = null;
            if (options.RestoreSessions)
            {
                sessionManifest = await ReadSessionBackupManifestAsync(normalizedBackupDir);
                await AssertSessionFilesWritableAsync(sessionManifest.Files.Select(static item => item.Path));
            }

            if (options.RestoreConfig)
            {
                await CopyIfPresentAsync(Path.Combine(normalizedBackupDir, "config.toml"), ConfigPath(codexHome), overwrite: true);
                await CopyIfPresentAsync(Path.Combine(normalizedBackupDir, GlobalStateFileBasename), GlobalStatePath(codexHome), overwrite: true);
                await CopyIfPresentAsync(Path.Combine(normalizedBackupDir, GlobalStateBackupFileBasename), GlobalStateBackupPath(codexHome), overwrite: true);
            }

            if (options.RestoreDatabase)
            {
                await AssertSqliteWritableAsync(codexHome);
                var dbDir = Path.Combine(normalizedBackupDir, "db");
                var backedUpFiles = new HashSet<string>(metadata.DbFiles, StringComparer.Ordinal);
                foreach (var suffix in new[] { string.Empty, "-shm", "-wal" })
                {
                    var fileName = $"{DbFileBasename}{suffix}";
                    var targetPath = Path.Combine(codexHome, fileName);
                    if (!backedUpFiles.Contains(fileName) && File.Exists(targetPath))
                    {
                        File.Delete(targetPath);
                    }
                }

                foreach (var fileName in metadata.DbFiles)
                {
                    await CopyIfPresentAsync(Path.Combine(dbDir, fileName), Path.Combine(codexHome, fileName), overwrite: true);
                }
            }

            if (options.RestoreSessions && sessionManifest is not null)
            {
                await RestoreSessionChangesAsync(sessionManifest.Files);
            }

            return new CodexHistoryRestoreResult(
                true,
                codexHome,
                normalizedBackupDir,
                $"已恢复 Codex 历史备份：{Path.GetFileName(normalizedBackupDir)}。",
                metadata.TargetProvider,
                metadata.CreatedAt,
                metadata.ChangedSessionFiles,
                null);
        }
        catch (Exception error)
        {
            return new CodexHistoryRestoreResult(
                false,
                codexHome,
                backupDir,
                "Codex 历史备份恢复失败。",
                null,
                null,
                0,
                error.Message);
        }
    }

    public Task<CodexHistoryBackupSummary> GetBackupSummaryAsync(string? explicitCodexHome = null)
    {
        var codexHome = NormalizeCodexHome(explicitCodexHome);
        var backupRoot = BackupRoot(codexHome);
        return Task.Run(() =>
        {
            if (!Directory.Exists(backupRoot))
            {
                return new CodexHistoryBackupSummary(backupRoot, 0, 0);
            }

            var entries = GetManagedBackupDirectories(backupRoot);
            return new CodexHistoryBackupSummary(
                backupRoot,
                entries.Count,
                entries.Sum(static item => GetDirectorySize(item.FullName)));
        });
    }

    public Task<CodexHistoryBackupPruneResult> PruneBackupsAsync(
        string? explicitCodexHome = null,
        int keepCount = DefaultBackupRetentionCount)
    {
        var codexHome = NormalizeCodexHome(explicitCodexHome);
        var backupRoot = BackupRoot(codexHome);
        return Task.Run(() =>
        {
            if (!Directory.Exists(backupRoot))
            {
                return new CodexHistoryBackupPruneResult(backupRoot, 0, 0, 0);
            }

            var entries = GetManagedBackupDirectories(backupRoot);
            var toDelete = entries.Skip(Math.Max(0, keepCount)).ToList();
            var freedBytes = 0L;
            foreach (var entry in toDelete)
            {
                freedBytes += GetDirectorySize(entry.FullName);
                entry.Delete(recursive: true);
            }

            return new CodexHistoryBackupPruneResult(
                backupRoot,
                toDelete.Count,
                entries.Count - toDelete.Count,
                freedBytes);
        });
    }

    public static string BuildProviderDisplayName(string provider)
        => provider switch
        {
            OfficialOpenAiProvider => "ChatGPT 官方",
            RelayBenchProvider => "RelayBench",
            _ => provider
        };

    private string NormalizeCodexHome(string? explicitCodexHome)
        => Path.GetFullPath(string.IsNullOrWhiteSpace(explicitCodexHome)
            ? Path.Combine(_environment.UserProfilePath, ".codex")
            : explicitCodexHome.Trim());

    private static string ResolveTargetProvider(CodexHistorySyncTarget target, string? explicitProvider)
        => target switch
        {
            CodexHistorySyncTarget.OfficialOpenAi => OfficialOpenAiProvider,
            CodexHistorySyncTarget.RelayBenchProvider => RelayBenchProvider,
            CodexHistorySyncTarget.ExplicitProvider => string.IsNullOrWhiteSpace(explicitProvider)
                ? RelayBenchProvider
                : explicitProvider.Trim(),
            _ => RelayBenchProvider
        };

    private static CodexHistorySyncResult BuildFailedResult(
        CodexHistorySyncTarget target,
        string targetProvider,
        string codexHome,
        string summary,
        string error)
        => new(
            false,
            target,
            targetProvider,
            codexHome,
            summary,
            0,
            [],
            0,
            0,
            0,
            0,
            0,
            0,
            false,
            null,
            [],
            null,
            error);

    private static string ConfigPath(string codexHome)
        => Path.Combine(codexHome, "config.toml");

    private static string BackupRoot(string codexHome)
        => Path.Combine(codexHome, "backups_state", BackupNamespace);

    private static string LockPath(string codexHome)
        => Path.Combine(codexHome, "tmp", "provider-sync.lock");

    private static string GlobalStatePath(string codexHome)
        => Path.Combine(codexHome, GlobalStateFileBasename);

    private static string GlobalStateBackupPath(string codexHome)
        => Path.Combine(codexHome, GlobalStateBackupFileBasename);

    private static CodexProviderCounts EmptyProviderCounts()
        => new(new Dictionary<string, int>(StringComparer.Ordinal), new Dictionary<string, int>(StringComparer.Ordinal));

    private static string BuildEncryptedContentWarning(CodexProviderCounts encryptedContentCounts, string targetProvider)
    {
        var riskyProviders = encryptedContentCounts.Sessions
            .Concat(encryptedContentCounts.ArchivedSessions)
            .Where(pair => pair.Value > 0 && !string.Equals(pair.Key, targetProvider, StringComparison.Ordinal))
            .Select(static pair => pair.Key)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (riskyProviders.Length == 0)
        {
            return string.Empty;
        }

        var total = encryptedContentCounts.Sessions.Values.Sum() + encryptedContentCounts.ArchivedSessions.Values.Sum();
        return $"检测到 {total} 份历史记录包含 encrypted_content。RelayBench 只会同步可见性元数据，但跨账号或跨 provider 继续这些历史时仍可能遇到 invalid_encrypted_content。原 provider：{string.Join("、", riskyProviders)}。";
    }

    private static async Task<LockHandle> AcquireLockAsync(string codexHome, string label)
    {
        var lockPath = LockPath(codexHome);
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        Directory.CreateDirectory(lockPath);
        var ownerPath = Path.Combine(lockPath, "owner.json");
        try
        {
            await using var ownerStream = new FileStream(
                ownerPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous);
            var owner = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
                new
                {
                    processId = Environment.ProcessId,
                    startedAt = DateTimeOffset.UtcNow,
                    label,
                    currentDirectory = Environment.CurrentDirectory
                },
                JsonOptions()));
            await ownerStream.WriteAsync(owner);
            return new LockHandle(lockPath);
        }
        catch (IOException error) when (File.Exists(ownerPath))
        {
            throw new InvalidOperationException($"Codex 历史同步锁已存在：{lockPath}。请确认没有其它同步任务正在运行。", error);
        }
        catch
        {
            if (Directory.Exists(lockPath) && !File.Exists(ownerPath))
            {
                Directory.Delete(lockPath, recursive: true);
            }

            throw;
        }
    }

    private sealed class LockHandle : IAsyncDisposable
    {
        private readonly string _lockPath;
        private bool _released;

        public LockHandle(string lockPath)
        {
            _lockPath = lockPath;
        }

        public ValueTask DisposeAsync()
        {
            if (_released)
            {
                return ValueTask.CompletedTask;
            }

            _released = true;
            if (Directory.Exists(_lockPath))
            {
                Directory.Delete(_lockPath, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }

    private static CurrentProviderInfo ReadCurrentProviderFromConfigText(string configText)
    {
        foreach (var rawLine in SplitLines(configText))
        {
            var trimmed = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#'))
            {
                continue;
            }

            if (trimmed.StartsWith('['))
            {
                break;
            }

            var match = Regex.Match(trimmed, "^model_provider\\s*=\\s*\"([^\"]+)\"\\s*$");
            if (match.Success)
            {
                return new CurrentProviderInfo(match.Groups[1].Value, false);
            }
        }

        return new CurrentProviderInfo(OfficialOpenAiProvider, true);
    }

    private static IReadOnlyList<string> ListConfiguredProviderIds(string configText)
    {
        var providerIds = new HashSet<string>(StringComparer.Ordinal)
        {
            OfficialOpenAiProvider
        };

        foreach (Match match in ProviderRegex().Matches(configText))
        {
            providerIds.Add(match.Groups[1].Value);
        }

        return providerIds.Order(StringComparer.Ordinal).ToArray();
    }

    private static bool WriteConfigProvider(string configPath, string originalConfigText, string targetProvider)
    {
        var nextConfigText = SetRootProviderInConfigText(originalConfigText, targetProvider);
        if (string.Equals(originalConfigText, nextConfigText, StringComparison.Ordinal))
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, nextConfigText);
        return true;
    }

    private static string SetRootProviderInConfigText(string configText, string provider)
    {
        var newline = configText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        if (string.IsNullOrWhiteSpace(configText))
        {
            return $"model_provider = {JsonSerializer.Serialize(provider)}{newline}";
        }

        var lines = SplitLines(configText).ToList();
        var insertIndex = lines.Count;

        for (var index = 0; index < lines.Count; index++)
        {
            var trimmed = lines[index].Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#'))
            {
                insertIndex = index + 1;
                continue;
            }

            if (trimmed.StartsWith('['))
            {
                insertIndex = index;
                break;
            }

            if (trimmed.StartsWith("model_provider", StringComparison.Ordinal) && trimmed.Contains('='))
            {
                lines[index] = $"model_provider = {JsonSerializer.Serialize(provider)}";
                return JoinLines(lines, newline, configText.EndsWith(newline, StringComparison.Ordinal));
            }

            insertIndex = index + 1;
        }

        lines.Insert(insertIndex, $"model_provider = {JsonSerializer.Serialize(provider)}");
        return JoinLines(lines, newline, configText.EndsWith(newline, StringComparison.Ordinal));
    }

    private static IEnumerable<string> SplitLines(string text)
        => text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

    private static string JoinLines(IReadOnlyList<string> lines, string newline, bool keepTrailingNewline)
    {
        var text = string.Join(newline, lines).TrimEnd();
        return string.IsNullOrWhiteSpace(text)
            ? string.Empty
            : text + (keepTrailingNewline ? newline : string.Empty);
    }

    [GeneratedRegex("""^\[model_providers\.([A-Za-z0-9_.-]+)]\s*$""", RegexOptions.Multiline)]
    private static partial Regex ProviderRegex();

    private sealed record CurrentProviderInfo(string Provider, bool Implicit);
    private sealed record FileSnapshot(long Length, long LastWriteTimeUtcTicks);
    private sealed record FirstLineRecord(string FirstLine, string Separator, int Offset);
    private sealed record SessionChange(
        string Path,
        string? ThreadId,
        string Directory,
        string OriginalFirstLine,
        string OriginalSeparator,
        int OriginalOffset,
        long OriginalFileLength,
        long OriginalLastWriteTimeUtcTicks,
        string OriginalProvider,
        string UpdatedFirstLine);

    private sealed record SessionChangeCollection(
        IReadOnlyList<SessionChange> Changes,
        IReadOnlyList<string> LockedPaths,
        CodexProviderCounts ProviderCounts,
        CodexProviderCounts EncryptedContentCounts,
        IReadOnlyCollection<string> UserEventThreadIds,
        IReadOnlyDictionary<string, string> ThreadCwdsById);

    private sealed record SessionApplyResult(
        int AppliedCount,
        IReadOnlyList<string> AppliedPaths,
        IReadOnlyList<string> SkippedPaths);

    private sealed record WorkspaceRootSyncResult(
        bool Present,
        bool Updated,
        int UpdatedWorkspaceRoots,
        int SavedWorkspaceRootCount);

    private sealed record ThreadCwdStat(
        string Cwd,
        string NormalizedCwd,
        long Count,
        long UpdatedAtMs);
}
