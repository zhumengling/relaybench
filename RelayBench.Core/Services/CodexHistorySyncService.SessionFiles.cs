using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RelayBench.Core.Models;

namespace RelayBench.Core.Services;

public sealed partial class CodexHistorySyncService
{
    private static async Task<SessionChangeCollection> CollectSessionChangesAsync(
        string codexHome,
        string targetProvider,
        bool skipLockedReads = false)
    {
        List<SessionChange> changes = [];
        List<string> lockedPaths = [];
        Dictionary<string, int> sessionCounts = new(StringComparer.Ordinal);
        Dictionary<string, int> archivedCounts = new(StringComparer.Ordinal);
        Dictionary<string, int> encryptedSessionCounts = new(StringComparer.Ordinal);
        Dictionary<string, int> encryptedArchivedCounts = new(StringComparer.Ordinal);
        HashSet<string> userEventThreadIds = new(StringComparer.Ordinal);
        Dictionary<string, string> threadCwdsById = new(StringComparer.Ordinal);

        foreach (var dirName in SessionDirectories)
        {
            var rootDir = Path.Combine(codexHome, dirName);
            if (!Directory.Exists(rootDir))
            {
                continue;
            }

            foreach (var rolloutPath in Directory.EnumerateFiles(rootDir, "rollout-*.jsonl", SearchOption.AllDirectories))
            {
                FirstLineRecord record;
                try
                {
                    record = await ReadFirstLineRecordAsync(rolloutPath);
                }
                catch (Exception error) when (skipLockedReads && IsRolloutFileBusyError(error))
                {
                    lockedPaths.Add(rolloutPath);
                    continue;
                }

                if (!TryParseSessionMetaRecord(record.FirstLine, out var root, out var payload))
                {
                    continue;
                }

                var currentProvider = GetString(payload["model_provider"]) ?? "(missing)";
                var bucket = string.Equals(dirName, "archived_sessions", StringComparison.Ordinal)
                    ? archivedCounts
                    : sessionCounts;
                bucket[currentProvider] = bucket.GetValueOrDefault(currentProvider) + 1;

                var threadId = GetString(payload["id"]);
                if (!string.IsNullOrWhiteSpace(threadId) &&
                    GetString(payload["cwd"]) is { Length: > 0 } cwd)
                {
                    threadCwdsById[threadId] = ToDesktopWorkspacePath(cwd);
                }

                bool hasEncryptedContent;
                try
                {
                    hasEncryptedContent = await FileHasEncryptedContentAsync(rolloutPath, record.FirstLine, record.Offset);
                    if (!string.IsNullOrWhiteSpace(threadId) &&
                        await FileHasUserEventAsync(rolloutPath, record.FirstLine, record.Offset))
                    {
                        userEventThreadIds.Add(threadId);
                    }
                }
                catch (Exception error) when (skipLockedReads && IsRolloutFileBusyError(error))
                {
                    lockedPaths.Add(rolloutPath);
                    continue;
                }

                if (hasEncryptedContent)
                {
                    var encryptedBucket = string.Equals(dirName, "archived_sessions", StringComparison.Ordinal)
                        ? encryptedArchivedCounts
                        : encryptedSessionCounts;
                    encryptedBucket[currentProvider] = encryptedBucket.GetValueOrDefault(currentProvider) + 1;
                }

                if (!string.Equals(targetProvider, StatusOnlyProvider, StringComparison.Ordinal) &&
                    !string.Equals(currentProvider, targetProvider, StringComparison.Ordinal))
                {
                    var snapshot = GetFileSnapshot(rolloutPath);
                    payload["model_provider"] = targetProvider;
                    changes.Add(new SessionChange(
                        rolloutPath,
                        threadId,
                        dirName,
                        record.FirstLine,
                        record.Separator,
                        record.Offset,
                        snapshot.Length,
                        snapshot.LastWriteTimeUtcTicks,
                        currentProvider,
                        root!.ToJsonString()));
                }
            }
        }

        return new SessionChangeCollection(
            changes,
            lockedPaths.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            new CodexProviderCounts(
                SortCounts(sessionCounts),
                SortCounts(archivedCounts)),
            new CodexProviderCounts(
                SortCounts(encryptedSessionCounts),
                SortCounts(encryptedArchivedCounts)),
            userEventThreadIds,
            threadCwdsById);
    }

    private static async Task<SessionApplyResult> ApplySessionChangesAsync(IReadOnlyList<SessionChange> changes)
    {
        List<string> appliedPaths = [];
        List<string> skippedPaths = [];

        foreach (var change in changes)
        {
            if (await TryRewriteCollectedSessionChangeAsync(change))
            {
                TryRestoreLastWriteTimeUtc(change.Path, change.OriginalLastWriteTimeUtcTicks);
                appliedPaths.Add(change.Path);
            }
            else
            {
                skippedPaths.Add(change.Path);
            }
        }

        appliedPaths.Sort(StringComparer.Ordinal);
        skippedPaths.Sort(StringComparer.Ordinal);
        return new SessionApplyResult(appliedPaths.Count, appliedPaths, skippedPaths);
    }

    private static async Task<(IReadOnlyList<SessionChange> WritableChanges, IReadOnlyList<SessionChange> LockedChanges)> SplitLockedSessionChangesAsync(
        IReadOnlyList<SessionChange> changes)
    {
        if (changes.Count == 0)
        {
            return (changes, []);
        }

        var lockedPaths = new HashSet<string>(
            await FindLockedFilesAsync(changes.Select(static change => change.Path)),
            StringComparer.Ordinal);
        if (lockedPaths.Count == 0)
        {
            return (changes, []);
        }

        List<SessionChange> writable = [];
        List<SessionChange> locked = [];
        foreach (var change in changes)
        {
            if (lockedPaths.Contains(change.Path))
            {
                locked.Add(change);
            }
            else
            {
                writable.Add(change);
            }
        }

        return (writable, locked);
    }

    private static async Task AssertSessionFilesWritableAsync(IEnumerable<string> filePaths)
    {
        var lockedPaths = await FindLockedFilesAsync(filePaths);
        if (lockedPaths.Count == 0)
        {
            return;
        }

        var preview = string.Join(", ", lockedPaths.Take(5));
        var suffix = lockedPaths.Count > 5 ? $"（另有 {lockedPaths.Count - 5} 个）" : string.Empty;
        throw new InvalidOperationException($"有 {lockedPaths.Count} 份 Codex 记录文件正在被占用，请关闭 Codex 后重试：{preview}{suffix}");
    }

    private static async Task RestoreSessionChangesAsync(IEnumerable<SessionBackupManifestEntry> manifestEntries)
    {
        foreach (var entry in manifestEntries)
        {
            await RewriteFirstLineAsync(entry.Path, entry.OriginalFirstLine, entry.OriginalSeparator);
            TryRestoreLastWriteTimeUtc(entry.Path, entry.OriginalLastWriteTimeUtcTicks);
        }
    }

    private static Task RestoreSessionChangesAsync(IEnumerable<SessionChange> changes)
        => RestoreSessionChangesAsync(changes.Select(static change => new SessionBackupManifestEntry
        {
            Path = change.Path,
            OriginalFirstLine = change.OriginalFirstLine,
            OriginalSeparator = change.OriginalSeparator,
            OriginalLastWriteTimeUtcTicks = change.OriginalLastWriteTimeUtcTicks
        }));

    private static async Task<FirstLineRecord> ReadFirstLineRecordAsync(string filePath)
    {
        try
        {
            await using FileStream stream = new(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await ReadFirstLineRecordAsync(stream);
        }
        catch (Exception error)
        {
            throw WrapRolloutFileBusyError(error, filePath, "读取");
        }
    }

    private static async Task<FirstLineRecord> ReadFirstLineRecordAsync(FileStream stream)
    {
        stream.Seek(0, SeekOrigin.Begin);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            using MemoryStream collected = new();
            while (true)
            {
                var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length));
                if (bytesRead == 0)
                {
                    break;
                }

                await collected.WriteAsync(buffer.AsMemory(0, bytesRead));
                var current = collected.GetBuffer().AsSpan(0, (int)collected.Length);
                var newlineIndex = current.IndexOf((byte)'\n');
                if (newlineIndex >= 0)
                {
                    var crlf = newlineIndex > 0 && current[newlineIndex - 1] == '\r';
                    var lineLength = crlf ? newlineIndex - 1 : newlineIndex;
                    return new FirstLineRecord(
                        Encoding.UTF8.GetString(current[..lineLength]),
                        crlf ? "\r\n" : "\n",
                        newlineIndex + 1);
                }
            }

            return new FirstLineRecord(
                Encoding.UTF8.GetString(collected.GetBuffer(), 0, (int)collected.Length),
                string.Empty,
                (int)collected.Length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool TryParseSessionMetaRecord(
        string firstLine,
        out JsonObject? root,
        out JsonObject payload)
    {
        root = null;
        payload = [];
        if (string.IsNullOrWhiteSpace(firstLine))
        {
            return false;
        }

        try
        {
            root = JsonNode.Parse(firstLine) as JsonObject;
            if (!string.Equals(GetString(root?["type"]), "session_meta", StringComparison.Ordinal) ||
                root?["payload"] is not JsonObject parsedPayload)
            {
                return false;
            }

            payload = parsedPayload;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async Task<bool> TryRewriteCollectedSessionChangeAsync(SessionChange change)
    {
        try
        {
            await using var sourceStream = OpenExclusiveRewriteStream(change.Path);
            if (sourceStream.Length != change.OriginalFileLength)
            {
                return false;
            }

            var current = await ReadFirstLineRecordAsync(sourceStream);
            if (!string.Equals(current.FirstLine, change.OriginalFirstLine, StringComparison.Ordinal) ||
                current.Offset != change.OriginalOffset)
            {
                return false;
            }

            await RewriteFirstLineAsync(
                sourceStream,
                change.Path,
                change.UpdatedFirstLine,
                change.OriginalSeparator,
                change.OriginalOffset,
                headerOnly: change.OriginalOffset >= change.OriginalFileLength);
            return true;
        }
        catch (Exception error) when (IsRolloutFileBusyError(error))
        {
            return false;
        }
    }

    private static async Task RewriteFirstLineAsync(string filePath, string nextFirstLine, string separator)
    {
        try
        {
            await using var sourceStream = OpenExclusiveRewriteStream(filePath);
            var current = await ReadFirstLineRecordAsync(sourceStream);
            var headerOnly = string.IsNullOrEmpty(current.Separator) &&
                             current.Offset == Encoding.UTF8.GetByteCount(current.FirstLine);
            await RewriteFirstLineAsync(sourceStream, filePath, nextFirstLine, separator, current.Offset, headerOnly);
        }
        catch (Exception error)
        {
            throw WrapRolloutFileBusyError(error, filePath, "改写");
        }
    }

    private static FileStream OpenExclusiveRewriteStream(string filePath)
    {
        try
        {
            return new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception error)
        {
            throw WrapRolloutFileBusyError(error, filePath, "改写");
        }
    }

    private static async Task RewriteFirstLineAsync(
        FileStream sourceStream,
        string filePath,
        string nextFirstLine,
        string separator,
        int sourceOffset,
        bool headerOnly)
    {
        var tempPath = $"{filePath}.provider-sync.{Environment.ProcessId}.{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.tmp";
        try
        {
            await using (FileStream writer = new(
                             tempPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await writer.WriteAsync(Encoding.UTF8.GetBytes(nextFirstLine));
                if (!string.IsNullOrEmpty(separator))
                {
                    await writer.WriteAsync(Encoding.UTF8.GetBytes(separator));
                }

                if (!headerOnly)
                {
                    sourceStream.Seek(sourceOffset, SeekOrigin.Begin);
                    await sourceStream.CopyToAsync(writer);
                }
            }

            await using (FileStream tempReader = new(
                             tempPath,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                sourceStream.SetLength(0);
                sourceStream.Seek(0, SeekOrigin.Begin);
                await tempReader.CopyToAsync(sourceStream);
                await sourceStream.FlushAsync();
            }
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    private static FileSnapshot GetFileSnapshot(string filePath)
    {
        var fileInfo = new FileInfo(filePath);
        return new FileSnapshot(fileInfo.Length, fileInfo.LastWriteTimeUtc.Ticks);
    }

    private static async Task<bool> FileHasEncryptedContentAsync(string filePath, string firstLine, int startOffset)
    {
        if (firstLine.Contains("encrypted_content", StringComparison.Ordinal))
        {
            return true;
        }

        return await FileContainsTextAsync(filePath, "encrypted_content", startOffset);
    }

    private static async Task<bool> FileContainsTextAsync(string filePath, string text, int startOffset)
    {
        var needle = Encoding.UTF8.GetBytes(text);
        var buffer = ArrayPool<byte>.Shared.Rent(ScanBufferSize);
        byte[] tail = [];

        try
        {
            await using FileStream stream = new(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                ScanBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (startOffset > 0)
            {
                stream.Seek(startOffset, SeekOrigin.Begin);
            }

            while (true)
            {
                var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, ScanBufferSize));
                if (bytesRead == 0)
                {
                    return false;
                }

                var haystack = buffer;
                var haystackLength = bytesRead;
                if (tail.Length > 0)
                {
                    haystackLength = tail.Length + bytesRead;
                    haystack = ArrayPool<byte>.Shared.Rent(haystackLength);
                    Buffer.BlockCopy(tail, 0, haystack, 0, tail.Length);
                    Buffer.BlockCopy(buffer, 0, haystack, tail.Length, bytesRead);
                }

                try
                {
                    if (ContainsNeedle(haystack, haystackLength, needle))
                    {
                        return true;
                    }

                    var keepBytes = Math.Min(Math.Max(0, needle.Length - 1), haystackLength);
                    tail = keepBytes == 0 ? [] : haystack[(haystackLength - keepBytes)..haystackLength].ToArray();
                }
                finally
                {
                    if (!ReferenceEquals(haystack, buffer))
                    {
                        ArrayPool<byte>.Shared.Return(haystack);
                    }
                }
            }
        }
        catch (Exception error)
        {
            throw WrapRolloutFileBusyError(error, filePath, "扫描");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool ContainsNeedle(byte[] haystack, int haystackLength, byte[] needle)
    {
        if (needle.Length == 0)
        {
            return true;
        }

        for (var index = 0; index <= haystackLength - needle.Length; index++)
        {
            var matched = true;
            for (var needleIndex = 0; needleIndex < needle.Length; needleIndex++)
            {
                if (haystack[index + needleIndex] != needle[needleIndex])
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<bool> FileHasUserEventAsync(string filePath, string firstLine, int startOffset)
    {
        try
        {
            if (RecordHasUserEvent(JsonNode.Parse(firstLine)))
            {
                return true;
            }
        }
        catch
        {
            // Keep scanning the rest of the rollout below.
        }

        try
        {
            await using FileStream stream = new(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (startOffset > 0)
            {
                stream.Seek(startOffset, SeekOrigin.Begin);
            }

            using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 64 * 1024);
            while (await reader.ReadLineAsync() is { } rawLine)
            {
                if (string.IsNullOrWhiteSpace(rawLine))
                {
                    continue;
                }

                try
                {
                    if (RecordHasUserEvent(JsonNode.Parse(rawLine)))
                    {
                        return true;
                    }
                }
                catch
                {
                    // Ignore malformed non-metadata lines.
                }
            }

            return false;
        }
        catch (Exception error)
        {
            throw WrapRolloutFileBusyError(error, filePath, "扫描");
        }
    }

    private static bool RecordHasUserEvent(JsonNode? record)
    {
        if (record is not JsonObject root)
        {
            return false;
        }

        if (string.Equals(GetString(root["type"]), "event_msg", StringComparison.Ordinal) &&
            root["payload"] is JsonObject eventPayload &&
            string.Equals(GetString(eventPayload["type"]), "user_message", StringComparison.Ordinal))
        {
            return true;
        }

        foreach (var key in new[] { "payload", "item", "msg" })
        {
            if (root[key] is JsonObject value &&
                string.Equals(GetString(value["type"]), "message", StringComparison.Ordinal) &&
                string.Equals(GetString(value["role"]), "user", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<IReadOnlyList<string>> FindLockedFilesAsync(IEnumerable<string> filePaths)
    {
        List<string> lockedPaths = [];
        foreach (var filePath in filePaths.Distinct(StringComparer.Ordinal))
        {
            if (!File.Exists(filePath))
            {
                continue;
            }

            try
            {
                await using FileStream stream = new(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (Exception error) when (IsRolloutFileBusyError(error))
            {
                lockedPaths.Add(filePath);
            }
        }

        lockedPaths.Sort(StringComparer.Ordinal);
        return lockedPaths;
    }

    private static bool IsRolloutFileBusyError(Exception error)
    {
        if (error.InnerException is not null && IsRolloutFileBusyError(error.InnerException))
        {
            return true;
        }

        if (error is IOException ioException)
        {
            var code = ioException.HResult & 0xFFFF;
            return code is 32 or 33;
        }

        return false;
    }

    private static Exception WrapRolloutFileBusyError(Exception error, string filePath, string action)
        => IsRolloutFileBusyError(error)
            ? new IOException($"无法{action} Codex 记录文件，文件正在被占用：{filePath}", error)
            : error;

    private static string? GetString(JsonNode? node)
    {
        try
        {
            return node?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }

    private static string ToDesktopWorkspacePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var trimmed = value.Trim();
        if (trimmed.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + trimmed[8..].Replace('/', '\\');
        }

        if (trimmed.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            var withoutPrefix = trimmed[4..].Replace('/', '\\');
            return withoutPrefix.Length == 2 && char.IsLetter(withoutPrefix[0]) && withoutPrefix[1] == ':'
                ? withoutPrefix + "\\"
                : withoutPrefix;
        }

        return value;
    }

    private static void TryRestoreLastWriteTimeUtc(string filePath, long? ticks)
    {
        if (ticks is null)
        {
            return;
        }

        try
        {
            File.SetLastWriteTimeUtc(filePath, new DateTime(ticks.Value, DateTimeKind.Utc));
        }
        catch
        {
            // Best effort only.
        }
    }

    private static void TryDeleteFile(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch
        {
            // Best effort cleanup.
        }
    }

    private static IReadOnlyDictionary<string, int> SortCounts(Dictionary<string, int> counts)
        => counts
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
}
