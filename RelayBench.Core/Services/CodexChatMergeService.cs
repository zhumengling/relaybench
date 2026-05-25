using RelayBench.Core.Models;

namespace RelayBench.Core.Services;

public sealed class CodexChatMergeService
{
    private readonly CodexHistorySyncService _historySyncService;

    public CodexChatMergeService(IClientApiConfigMutationEnvironment? environment = null)
    {
        _historySyncService = new CodexHistorySyncService(environment);
    }

    public async Task<CodexChatMergeResult> MergeAsync(
        CodexChatMergeTarget target,
        string? targetModel = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = targetModel;

        var syncTarget = target == CodexChatMergeTarget.OfficialOpenAi
            ? CodexHistorySyncTarget.OfficialOpenAi
            : CodexHistorySyncTarget.RelayBenchProvider;
        var result = await _historySyncService.SyncAsync(syncTarget);

        return new CodexChatMergeResult(
            result.Succeeded,
            target,
            result.Summary,
            result.SqliteProviderRowsUpdated,
            result.ChangedSessionFileCount,
            result.ChangedFiles,
            string.IsNullOrWhiteSpace(result.BackupDir) ? [] : [result.BackupDir],
            BuildWarning(result),
            result.Error);
    }

    public static string BuildTargetDisplayName(CodexChatMergeTarget target)
        => target switch
        {
            CodexChatMergeTarget.OfficialOpenAi => "ChatGPT 官方",
            CodexChatMergeTarget.ThirdPartyCustom => "RelayBench",
            _ => "当前 provider"
        };

    private static string? BuildWarning(CodexHistorySyncResult result)
    {
        List<string> warnings = [];
        if (!string.IsNullOrWhiteSpace(result.Warning))
        {
            warnings.Add(result.Warning);
        }

        if (result.SkippedLockedRolloutFiles.Count > 0)
        {
            var preview = string.Join("\n", result.SkippedLockedRolloutFiles.Take(8));
            var suffix = result.SkippedLockedRolloutFiles.Count > 8
                ? $"\n另有 {result.SkippedLockedRolloutFiles.Count - 8} 个文件未列出。"
                : string.Empty;
            warnings.Add($"有 {result.SkippedLockedRolloutFiles.Count} 份 Codex 记录文件被占用，已跳过：\n{preview}{suffix}");
        }

        if (result.UpdatedWorkspaceRoots > 0)
        {
            warnings.Add($"已同步 Codex Desktop 工作区可见性：{result.UpdatedWorkspaceRoots} 项。");
        }

        return warnings.Count == 0 ? null : string.Join(Environment.NewLine, warnings);
    }
}
