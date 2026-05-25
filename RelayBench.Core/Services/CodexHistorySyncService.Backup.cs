using System.Text.Json;

namespace RelayBench.Core.Services;

public sealed partial class CodexHistorySyncService
{
    private static async Task<string> CreateBackupAsync(
        string codexHome,
        string targetProvider,
        IReadOnlyList<SessionChange> sessionChanges,
        string configBackupText)
    {
        var backupRoot = BackupRoot(codexHome);
        var backupDir = Path.Combine(backupRoot, DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'"));
        var dbDir = Path.Combine(backupDir, "db");
        Directory.CreateDirectory(dbDir);

        List<string> copiedDbFiles = [];
        foreach (var suffix in new[] { string.Empty, "-shm", "-wal" })
        {
            var fileName = $"{DbFileBasename}{suffix}";
            if (await CopyIfPresentAsync(Path.Combine(codexHome, fileName), Path.Combine(dbDir, fileName), overwrite: false))
            {
                copiedDbFiles.Add(fileName);
            }
        }

        var configBackupPath = Path.Combine(backupDir, "config.toml");
        await File.WriteAllTextAsync(configBackupPath, configBackupText);
        await CopyIfPresentAsync(GlobalStatePath(codexHome), Path.Combine(backupDir, GlobalStateFileBasename), overwrite: false);
        await CopyIfPresentAsync(GlobalStateBackupPath(codexHome), Path.Combine(backupDir, GlobalStateBackupFileBasename), overwrite: false);

        var createdAt = DateTimeOffset.UtcNow;
        var sessionManifest = new SessionBackupManifest
        {
            Version = 1,
            Namespace = BackupNamespace,
            CodexHome = codexHome,
            TargetProvider = targetProvider,
            CreatedAt = createdAt,
            Files = sessionChanges.Select(static change => new SessionBackupManifestEntry
            {
                Path = change.Path,
                OriginalFirstLine = change.OriginalFirstLine,
                OriginalSeparator = change.OriginalSeparator,
                OriginalLastWriteTimeUtcTicks = change.OriginalLastWriteTimeUtcTicks
            }).ToList()
        };
        await File.WriteAllTextAsync(
            Path.Combine(backupDir, "session-meta-backup.json"),
            JsonSerializer.Serialize(sessionManifest, JsonOptions()));

        var metadata = new BackupMetadataFile
        {
            Version = 1,
            Namespace = BackupNamespace,
            CodexHome = codexHome,
            TargetProvider = targetProvider,
            CreatedAt = createdAt,
            DbFiles = copiedDbFiles,
            ChangedSessionFiles = sessionChanges.Count
        };
        await File.WriteAllTextAsync(
            Path.Combine(backupDir, "metadata.json"),
            JsonSerializer.Serialize(metadata, JsonOptions()));

        return backupDir;
    }

    private static async Task UpdateSessionBackupManifestAsync(string backupDir, IReadOnlyList<SessionChange> sessionChanges)
    {
        var normalizedBackupDir = Path.GetFullPath(backupDir);
        var manifestPath = Path.Combine(normalizedBackupDir, "session-meta-backup.json");
        var metadataPath = Path.Combine(normalizedBackupDir, "metadata.json");

        var sessionManifest = await ReadSessionBackupManifestAsync(normalizedBackupDir);
        var metadata = await ReadBackupMetadataAsync(normalizedBackupDir);

        sessionManifest = sessionManifest with
        {
            Files = sessionChanges.Select(static change => new SessionBackupManifestEntry
            {
                Path = change.Path,
                OriginalFirstLine = change.OriginalFirstLine,
                OriginalSeparator = change.OriginalSeparator,
                OriginalLastWriteTimeUtcTicks = change.OriginalLastWriteTimeUtcTicks
            }).ToList()
        };
        metadata = metadata with
        {
            ChangedSessionFiles = sessionChanges.Count
        };

        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(sessionManifest, JsonOptions()));
        await File.WriteAllTextAsync(metadataPath, JsonSerializer.Serialize(metadata, JsonOptions()));
    }

    private static async Task RestoreGlobalStateFilesAsync(string backupDir, string codexHome)
    {
        var normalizedBackupDir = Path.GetFullPath(backupDir);
        await CopyIfPresentAsync(Path.Combine(normalizedBackupDir, GlobalStateFileBasename), GlobalStatePath(codexHome), overwrite: true);
        await CopyIfPresentAsync(Path.Combine(normalizedBackupDir, GlobalStateBackupFileBasename), GlobalStateBackupPath(codexHome), overwrite: true);
    }

    private static async Task<bool> CopyIfPresentAsync(string sourcePath, string destinationPath, bool overwrite)
    {
        if (!File.Exists(sourcePath))
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        File.Copy(sourcePath, destinationPath, overwrite);
        await Task.CompletedTask;
        return true;
    }

    private static async Task<BackupMetadataFile> ReadBackupMetadataAsync(string backupDir)
    {
        var metadataPath = Path.Combine(backupDir, "metadata.json");
        var metadata = JsonSerializer.Deserialize<BackupMetadataFile>(
            await File.ReadAllTextAsync(metadataPath),
            JsonOptions());
        if (metadata is null || !string.Equals(metadata.Namespace, BackupNamespace, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"备份元数据无效：{backupDir}");
        }

        return metadata;
    }

    private static async Task<SessionBackupManifest> ReadSessionBackupManifestAsync(string backupDir)
    {
        var manifestPath = Path.Combine(backupDir, "session-meta-backup.json");
        var manifest = JsonSerializer.Deserialize<SessionBackupManifest>(
            await File.ReadAllTextAsync(manifestPath),
            JsonOptions());
        if (manifest is null || !string.Equals(manifest.Namespace, BackupNamespace, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"记录文件备份清单无效：{backupDir}");
        }

        return manifest;
    }

    private static List<DirectoryInfo> GetManagedBackupDirectories(string backupRoot)
        => new DirectoryInfo(backupRoot)
            .EnumerateDirectories()
            .Where(static entry => IsManagedBackupDirectory(entry.FullName))
            .OrderByDescending(static entry => entry.Name, StringComparer.Ordinal)
            .ToList();

    private static bool IsManagedBackupDirectory(string backupDirectoryPath)
    {
        var metadataPath = Path.Combine(backupDirectoryPath, "metadata.json");
        if (!File.Exists(metadataPath))
        {
            return false;
        }

        try
        {
            var metadata = JsonSerializer.Deserialize<BackupMetadataFile>(
                File.ReadAllText(metadataPath),
                JsonOptions());
            return string.Equals(metadata?.Namespace, BackupNamespace, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static long GetDirectorySize(string directoryPath)
        => Directory.Exists(directoryPath)
            ? Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories)
                .Sum(static filePath => new FileInfo(filePath).Length)
            : 0;

    private static JsonSerializerOptions JsonOptions()
        => new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };

    private sealed record BackupMetadataFile
    {
        public int Version { get; init; }
        public required string Namespace { get; init; }
        public required string CodexHome { get; init; }
        public required string TargetProvider { get; init; }
        public required DateTimeOffset CreatedAt { get; init; }
        public required List<string> DbFiles { get; init; }
        public int ChangedSessionFiles { get; init; }
    }

    private sealed record SessionBackupManifest
    {
        public int Version { get; init; }
        public required string Namespace { get; init; }
        public required string CodexHome { get; init; }
        public required string TargetProvider { get; init; }
        public required DateTimeOffset CreatedAt { get; init; }
        public required List<SessionBackupManifestEntry> Files { get; init; }
    }

    private sealed record SessionBackupManifestEntry
    {
        public required string Path { get; init; }
        public required string OriginalFirstLine { get; init; }
        public required string OriginalSeparator { get; init; }
        public long? OriginalLastWriteTimeUtcTicks { get; init; }
    }
}
