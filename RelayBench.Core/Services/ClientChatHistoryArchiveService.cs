using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using RelayBench.Core.Models;

namespace RelayBench.Core.Services;

public sealed class ClientChatHistoryArchiveService
{
    private const string ManifestPath = "manifest.json";
    private const string ManifestSchema = "relaybench.chat-history-archive.v1";
    private const int ManifestVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IClientApiConfigMutationEnvironment _environment;

    public ClientChatHistoryArchiveService(IClientApiConfigMutationEnvironment? environment = null)
    {
        _environment = environment ?? new ClientApiDiagnosticEnvironment();
    }

    public async Task<ClientChatHistoryArchiveResult> ExportAsync(
        string archivePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);

        var fullArchivePath = Path.GetFullPath(archivePath);
        var archiveDirectory = Path.GetDirectoryName(fullArchivePath);
        if (!string.IsNullOrWhiteSpace(archiveDirectory))
        {
            Directory.CreateDirectory(archiveDirectory);
        }

        if (File.Exists(fullArchivePath))
        {
            File.Delete(fullArchivePath);
        }

        List<ClientChatHistoryArchiveEntry> entries = [];
        await using (var file = new FileStream(fullArchivePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
        {
            await AddCodexHistoryAsync(archive, entries, cancellationToken).ConfigureAwait(false);
            await AddClaudeHistoryAsync(archive, entries, cancellationToken).ConfigureAwait(false);

            var manifest = new ClientChatHistoryArchiveManifest(
                ManifestSchema,
                ManifestVersion,
                DateTimeOffset.UtcNow,
                entries);
            var manifestEntry = archive.CreateEntry(ManifestPath, CompressionLevel.Optimal);
            await using var manifestStream = manifestEntry.Open();
            await JsonSerializer.SerializeAsync(manifestStream, manifest, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }

        var codexCount = entries.Count(static item => item.Client.Equals("codex", StringComparison.OrdinalIgnoreCase));
        var claudeCount = entries.Count(static item => item.Client.Equals("claude", StringComparison.OrdinalIgnoreCase));
        var summary = $"聊天记录已导出：Codex {codexCount} 个文件，Claude {claudeCount} 个文件";
        if (entries.Count == 0)
        {
            summary = "未找到可导出的 Codex 或 Claude 聊天记录，已生成空归档清单";
        }

        return new ClientChatHistoryArchiveResult(true, summary, fullArchivePath, codexCount, claudeCount);
    }

    public async Task<ClientChatHistoryArchiveResult> ImportAsync(
        string archivePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);

        var fullArchivePath = Path.GetFullPath(archivePath);
        if (!File.Exists(fullArchivePath))
        {
            return new ClientChatHistoryArchiveResult(
                false,
                "聊天记录导入失败：归档文件不存在",
                fullArchivePath,
                0,
                0,
                Error: "missing-archive");
        }

        using var archive = ZipFile.OpenRead(fullArchivePath);
        var manifestEntry = archive.GetEntry(ManifestPath);
        if (manifestEntry is null)
        {
            return BuildInvalidArchiveResult(fullArchivePath, "缺少 manifest.json");
        }

        ClientChatHistoryArchiveManifest? manifest;
        await using (var manifestStream = manifestEntry.Open())
        {
            manifest = await JsonSerializer.DeserializeAsync<ClientChatHistoryArchiveManifest>(
                    manifestStream,
                    JsonOptions,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (manifest is null ||
            !string.Equals(manifest.Schema, ManifestSchema, StringComparison.Ordinal) ||
            manifest.Version != ManifestVersion)
        {
            return BuildInvalidArchiveResult(fullArchivePath, "manifest 不是 RelayBench 聊天记录归档");
        }

        var backupRoot = CreateImportBackup();
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var importedCodex = 0;
        var importedClaude = 0;
        var skipped = 0;

        BackupKnownHistoryScopes(backupRoot);

        foreach (var item in manifest.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!TryResolveImportTarget(item, out var targetPath))
            {
                skipped++;
                continue;
            }

            var sourceEntry = archive.GetEntry(item.ArchivePath);
            if (sourceEntry is null)
            {
                skipped++;
                continue;
            }

            var tempFile = Path.Combine(Path.GetTempPath(), $"relaybench-chat-import-{Guid.NewGuid():N}.tmp");
            try
            {
                await ExtractToTempFileAsync(sourceEntry, tempFile, cancellationToken).ConfigureAwait(false);
                var importedHash = await ComputeSha256Async(tempFile, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(importedHash, item.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    skipped++;
                    continue;
                }

                var finalTargetPath = targetPath;
                if (File.Exists(finalTargetPath))
                {
                    var existingHash = await ComputeSha256Async(finalTargetPath, cancellationToken).ConfigureAwait(false);
                    if (string.Equals(existingHash, item.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        skipped++;
                        continue;
                    }

                    finalTargetPath = BuildConflictPath(finalTargetPath, timestamp);
                }

                var targetDirectory = Path.GetDirectoryName(finalTargetPath);
                if (!string.IsNullOrWhiteSpace(targetDirectory))
                {
                    Directory.CreateDirectory(targetDirectory);
                }

                File.Copy(tempFile, finalTargetPath, overwrite: false);
                if (item.Client.Equals("codex", StringComparison.OrdinalIgnoreCase))
                {
                    importedCodex++;
                }
                else if (item.Client.Equals("claude", StringComparison.OrdinalIgnoreCase))
                {
                    importedClaude++;
                }
            }
            finally
            {
                TryDelete(tempFile);
            }
        }

        var summary = $"聊天记录导入完成：Codex {importedCodex} 个文件，Claude {importedClaude} 个文件，跳过 {skipped} 个文件";
        return new ClientChatHistoryArchiveResult(
            true,
            summary,
            fullArchivePath,
            importedCodex,
            importedClaude,
            backupRoot);
    }

    private async Task AddCodexHistoryAsync(
        ZipArchive archive,
        List<ClientChatHistoryArchiveEntry> entries,
        CancellationToken cancellationToken)
    {
        var codexHome = Path.Combine(_environment.UserProfilePath, ".codex");
        await AddDirectoryAsync(archive, entries, "codex", codexHome, "sessions", cancellationToken).ConfigureAwait(false);
        await AddDirectoryAsync(archive, entries, "codex", codexHome, "archived_sessions", cancellationToken).ConfigureAwait(false);

        foreach (var fileName in new[]
                 {
                     "state_5.sqlite",
                     "state_5.sqlite-wal",
                     "state_5.sqlite-shm",
                     "session_index.jsonl",
                     ".codex-global-state.json"
                 })
        {
            await AddFileAsync(
                    archive,
                    entries,
                    "codex",
                    codexHome,
                    Path.Combine(codexHome, fileName),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task AddClaudeHistoryAsync(
        ZipArchive archive,
        List<ClientChatHistoryArchiveEntry> entries,
        CancellationToken cancellationToken)
    {
        var claudeHome = Path.Combine(_environment.UserProfilePath, ".claude");
        await AddDirectoryAsync(archive, entries, "claude", claudeHome, "projects", cancellationToken).ConfigureAwait(false);
    }

    private async Task AddDirectoryAsync(
        ZipArchive archive,
        List<ClientChatHistoryArchiveEntry> entries,
        string client,
        string root,
        string relativeDirectory,
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(root, relativeDirectory);
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var filePath in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            await AddFileAsync(archive, entries, client, root, filePath, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task AddFileAsync(
        ZipArchive archive,
        List<ClientChatHistoryArchiveEntry> entries,
        string client,
        string root,
        string filePath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            return;
        }

        var relative = NormalizeArchivePath(Path.GetRelativePath(root, filePath));
        var archivePath = $"{client}/{relative}";
        var hash = await ComputeSha256Async(filePath, cancellationToken).ConfigureAwait(false);
        var fileInfo = new FileInfo(filePath);
        var zipEntry = archive.CreateEntry(archivePath, CompressionLevel.Optimal);
        await using (var source = OpenReadShared(filePath))
        await using (var target = zipEntry.Open())
        {
            await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
        }

        entries.Add(new ClientChatHistoryArchiveEntry(
            client,
            relative,
            archivePath,
            fileInfo.Length,
            hash));
    }

    private static async Task ExtractToTempFileAsync(
        ZipArchiveEntry entry,
        string tempFile,
        CancellationToken cancellationToken)
    {
        await using var source = entry.Open();
        await using var target = new FileStream(
            tempFile,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous);
        await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = OpenReadShared(filePath);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static FileStream OpenReadShared(string path)
        => new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            128 * 1024,
            FileOptions.SequentialScan | FileOptions.Asynchronous);

    private void BackupKnownHistoryScopes(string backupRoot)
    {
        var codexHome = Path.Combine(_environment.UserProfilePath, ".codex");
        BackupDirectory(Path.Combine(codexHome, "sessions"), Path.Combine(backupRoot, "codex", "sessions"));
        BackupDirectory(Path.Combine(codexHome, "archived_sessions"), Path.Combine(backupRoot, "codex", "archived_sessions"));
        foreach (var fileName in new[]
                 {
                     "state_5.sqlite",
                     "state_5.sqlite-wal",
                     "state_5.sqlite-shm",
                     "session_index.jsonl",
                     ".codex-global-state.json"
                 })
        {
            BackupFile(Path.Combine(codexHome, fileName), Path.Combine(backupRoot, "codex", fileName));
        }

        var claudeHome = Path.Combine(_environment.UserProfilePath, ".claude");
        BackupDirectory(Path.Combine(claudeHome, "projects"), Path.Combine(backupRoot, "claude", "projects"));
    }

    private static void BackupDirectory(string sourceDirectory, string targetDirectory)
    {
        if (!Directory.Exists(sourceDirectory))
        {
            return;
        }

        foreach (var sourcePath in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, sourcePath);
            BackupFile(sourcePath, Path.Combine(targetDirectory, relativePath));
        }
    }

    private static void BackupFile(string sourcePath, string targetPath)
    {
        if (!File.Exists(sourcePath))
        {
            return;
        }

        var directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var source = OpenReadShared(sourcePath);
        using var target = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
        source.CopyTo(target);
    }

    private string CreateImportBackup()
    {
        var backupRoot = Path.Combine(
            _environment.LocalAppDataPath,
            "RelayBench",
            "chat-history-import-backups",
            DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(backupRoot);
        return backupRoot;
    }

    private bool TryResolveImportTarget(ClientChatHistoryArchiveEntry item, out string targetPath)
    {
        targetPath = string.Empty;
        var relative = NormalizeArchivePath(item.RelativePath);
        if (relative.Contains("..", StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
        {
            return false;
        }

        if (item.Client.Equals("codex", StringComparison.OrdinalIgnoreCase) &&
            IsAllowedCodexRelativePath(relative))
        {
            targetPath = Path.Combine(_environment.UserProfilePath, ".codex", ToNativePath(relative));
            return true;
        }

        if (item.Client.Equals("claude", StringComparison.OrdinalIgnoreCase) &&
            relative.StartsWith("projects/", StringComparison.OrdinalIgnoreCase))
        {
            targetPath = Path.Combine(_environment.UserProfilePath, ".claude", ToNativePath(relative));
            return true;
        }

        return false;
    }

    private static bool IsAllowedCodexRelativePath(string relative)
        => relative.StartsWith("sessions/", StringComparison.OrdinalIgnoreCase) ||
           relative.StartsWith("archived_sessions/", StringComparison.OrdinalIgnoreCase) ||
           relative.Equals("state_5.sqlite", StringComparison.OrdinalIgnoreCase) ||
           relative.Equals("state_5.sqlite-wal", StringComparison.OrdinalIgnoreCase) ||
           relative.Equals("state_5.sqlite-shm", StringComparison.OrdinalIgnoreCase) ||
           relative.Equals("session_index.jsonl", StringComparison.OrdinalIgnoreCase) ||
           relative.Equals(".codex-global-state.json", StringComparison.OrdinalIgnoreCase);

    private static string BuildConflictPath(string targetPath, string timestamp)
    {
        var directory = Path.GetDirectoryName(targetPath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(targetPath);
        var extension = Path.GetExtension(targetPath);
        var candidate = Path.Combine(directory, $"{name}.relaybench-import-{timestamp}{extension}");
        var suffix = 2;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(directory, $"{name}.relaybench-import-{timestamp}-{suffix}{extension}");
            suffix++;
        }

        return candidate;
    }

    private static ClientChatHistoryArchiveResult BuildInvalidArchiveResult(string archivePath, string reason)
        => new(false, $"聊天记录导入失败：{reason}", archivePath, 0, 0, Error: "invalid-archive");

    private static string NormalizeArchivePath(string path)
        => path.Replace('\\', '/').TrimStart('/');

    private static string ToNativePath(string path)
        => path.Replace('/', Path.DirectorySeparatorChar);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
