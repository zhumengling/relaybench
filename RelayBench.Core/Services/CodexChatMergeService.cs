using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using RelayBench.Core.Models;

namespace RelayBench.Core.Services;

public sealed class CodexChatMergeService
{
    private const string OpenAiProvider = "openai";
    private const string CustomProvider = "custom";

    private readonly IClientApiConfigMutationEnvironment _environment;

    public CodexChatMergeService(IClientApiConfigMutationEnvironment? environment = null)
    {
        _environment = environment ?? new ClientApiDiagnosticEnvironment();
    }

    public async Task<CodexChatMergeResult> MergeAsync(
        CodexChatMergeTarget target,
        string? targetModel = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Keep historical model metadata intact; only provider buckets are merged.
        _ = targetModel;

        var codexRoot = Path.Combine(_environment.UserProfilePath, ".codex");
        if (!_environment.DirectoryExists(codexRoot))
        {
            return new CodexChatMergeResult(
                false,
                target,
                "未发现 .codex 目录，暂时无法整理聊天记录。",
                0,
                0,
                [],
                [],
                Error: "missing-codex-root");
        }
        RelayBenchBackupRetention.PruneAllUnderDirectory(_environment, codexRoot);

        var stateDatabasePath = Path.Combine(codexRoot, "state_5.sqlite");
        if (!_environment.FileExists(stateDatabasePath))
        {
            return new CodexChatMergeResult(
                false,
                target,
                "未发现 Codex 本地状态文件 state_5.sqlite，暂时无法整理聊天记录。",
                0,
                0,
                [],
                [],
                Error: "missing-state-db");
        }

        var targetProvider = ResolveTargetProvider(target);
        var backupFiles = new List<string>();
        var changedFiles = new List<string>();

        try
        {
            var threadRecords = await LoadThreadRecordsToMergeAsync(
                stateDatabasePath,
                targetProvider,
                cancellationToken);
            if (threadRecords.Count == 0)
            {
                return new CodexChatMergeResult(
                    true,
                    target,
                    $"当前没有需要合并到“{BuildTargetDisplayName(target)}”的其它来源聊天记录，已保持现状。",
                    0,
                    0,
                    [],
                    [],
                    null,
                    null);
            }

            BackupStateDatabaseArtifacts(stateDatabasePath, backupFiles);
            var rebuckettedThreadCount = await RebucketAllThreadsAsync(
                stateDatabasePath,
                targetProvider,
                cancellationToken);
            changedFiles.Add(stateDatabasePath);

            var updatedSessionFileCount = UpdateSessionFiles(
                threadRecords,
                targetProvider,
                backupFiles,
                changedFiles);

            string? warning = null;
            if (updatedSessionFileCount < threadRecords.Count)
            {
                warning = $"已合并 {rebuckettedThreadCount} 条聊天索引，但只同步更新了 {updatedSessionFileCount}/{threadRecords.Count} 份本地记录文件；未更新的文件可能不存在、为空或不包含 session_meta。";
            }

            var summary =
                $"已将 {rebuckettedThreadCount} 条历史 Codex 聊天统一整理到“{BuildTargetDisplayName(target)}”下；同时更新了 {updatedSessionFileCount} 份本地记录文件。";

            return new CodexChatMergeResult(
                true,
                target,
                summary,
                rebuckettedThreadCount,
                updatedSessionFileCount,
                changedFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                backupFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                warning,
                null);
        }
        catch (Exception ex)
        {
            return new CodexChatMergeResult(
                false,
                target,
                "聊天记录整理失败。",
                0,
                0,
                changedFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                backupFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                null,
                ex.Message);
        }
    }

    private async Task<IReadOnlyList<CodexThreadRecord>> LoadThreadRecordsToMergeAsync(
        string stateDatabasePath,
        string targetProvider,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(BuildConnectionString(stateDatabasePath));
        await connection.OpenAsync(cancellationToken);

        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA busy_timeout = 5000;";
            await pragma.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, rollout_path, model_provider, model
            FROM threads
            WHERE COALESCE(model_provider, '') <> $targetProvider
            """;
        command.Parameters.AddWithValue("$targetProvider", targetProvider);

        List<CodexThreadRecord> records = [];
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(new CodexThreadRecord(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return records;
    }

    private async Task<int> RebucketAllThreadsAsync(
        string stateDatabasePath,
        string targetProvider,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(BuildConnectionString(stateDatabasePath));
        await connection.OpenAsync(cancellationToken);

        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA busy_timeout = 5000;";
            await pragma.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE threads
            SET model_provider = $targetProvider
            WHERE COALESCE(model_provider, '') <> $targetProvider
            """;
        command.Parameters.AddWithValue("$targetProvider", targetProvider);

        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return affected;
    }

    private int UpdateSessionFiles(
        IReadOnlyList<CodexThreadRecord> threadRecords,
        string targetProvider,
        List<string> backupFiles,
        List<string> changedFiles)
    {
        var updatedCount = 0;
        var processedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var thread in threadRecords)
        {
            if (string.IsNullOrWhiteSpace(thread.RolloutPath) ||
                !processedPaths.Add(thread.RolloutPath) ||
                !_environment.FileExists(thread.RolloutPath))
            {
                continue;
            }

            try
            {
                var originalContent = _environment.ReadFileText(thread.RolloutPath);
                if (string.IsNullOrWhiteSpace(originalContent))
                {
                    continue;
                }

                if (!TryRewriteSessionMetaProvider(
                        originalContent,
                        targetProvider,
                        out var updatedContent))
                {
                    continue;
                }

                var backupPath = $"{thread.RolloutPath}.relaybench-backup-{DateTime.Now:yyyyMMddHHmmss}";
                _environment.CopyFile(thread.RolloutPath, backupPath, overwrite: true);
                backupFiles.Add(backupPath);
                RelayBenchBackupRetention.PruneForOriginalFile(_environment, thread.RolloutPath);
                _environment.WriteFileText(thread.RolloutPath, updatedContent);
                changedFiles.Add(thread.RolloutPath);
                updatedCount++;
            }
            catch
            {
                // Codex may keep the active session JSONL locked. The SQLite index is authoritative
                // for the sidebar, so skip locked files instead of aborting the whole merge.
            }
        }

        return updatedCount;
    }

    private static bool TryRewriteSessionMetaProvider(
        string content,
        string targetProvider,
        out string updatedContent)
    {
        updatedContent = content;
        var lines = content
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

        var changed = false;
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonNode? rootNode;
            try
            {
                rootNode = JsonNode.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            if (rootNode is not JsonObject rootObject ||
                !string.Equals(rootObject["type"]?.GetValue<string>(), "session_meta", StringComparison.Ordinal) ||
                rootObject["payload"] is not JsonObject payloadObject)
            {
                continue;
            }

            var currentProvider = payloadObject["model_provider"]?.GetValue<string>();
            if (string.Equals(currentProvider, targetProvider, StringComparison.Ordinal))
            {
                continue;
            }

            payloadObject["model_provider"] = targetProvider;
            lines[index] = rootObject.ToJsonString();
            changed = true;
        }

        if (!changed)
        {
            return false;
        }

        updatedContent = string.Join(Environment.NewLine, lines);
        return true;
    }

    private void BackupStateDatabaseArtifacts(string stateDatabasePath, List<string> backupFiles)
    {
        foreach (var path in EnumerateDatabaseArtifacts(stateDatabasePath))
        {
            if (!_environment.FileExists(path))
            {
                continue;
            }

            var backupPath = $"{path}.relaybench-backup-{DateTime.Now:yyyyMMddHHmmss}";
            _environment.CopyFile(path, backupPath, overwrite: true);
            backupFiles.Add(backupPath);
            RelayBenchBackupRetention.PruneForOriginalFile(_environment, path);
        }
    }

    private static IReadOnlyList<string> EnumerateDatabaseArtifacts(string stateDatabasePath)
        => [
            stateDatabasePath,
            $"{stateDatabasePath}-wal",
            $"{stateDatabasePath}-shm"
        ];

    private static string BuildConnectionString(string stateDatabasePath)
        => new SqliteConnectionStringBuilder
        {
            DataSource = stateDatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString();

    private static string ResolveTargetProvider(CodexChatMergeTarget target)
        => target switch
        {
            CodexChatMergeTarget.OfficialOpenAi => OpenAiProvider,
            CodexChatMergeTarget.ThirdPartyCustom => CustomProvider,
            _ => CustomProvider
        };

    public static string BuildTargetDisplayName(CodexChatMergeTarget target)
        => target switch
        {
            CodexChatMergeTarget.OfficialOpenAi => "ChatGPT 官方",
            CodexChatMergeTarget.ThirdPartyCustom => "第三方",
            _ => "当前"
        };

    private sealed record CodexThreadRecord(
        string ThreadId,
        string? RolloutPath,
        string? SourceProvider,
        string? SourceModel);
}
