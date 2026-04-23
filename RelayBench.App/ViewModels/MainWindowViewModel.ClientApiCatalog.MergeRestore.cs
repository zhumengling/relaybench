using RelayBench.Core.Models;

namespace RelayBench.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private async Task RestoreClientApiDefaultConfigWithMergeAsync(ClientApiCheck check)
    {
        var confirmed = await ShowConfirmationDialogAsync(
            "确认还原默认配置",
            $"确定要还原 {check.Name} 的默认配置吗？",
            "这会尝试清理代理接管或自定义入口配置，并在修改前自动创建备份文件。",
            "还原默认配置",
            "取消");

        if (!confirmed)
        {
            StatusMessage = $"已取消还原 {check.Name} 的默认配置。";
            return;
        }

        var shouldMergeChats = IsCodexChatMergeClient(check.Name) &&
                               await ConfirmCodexChatMergeAsync(
                                   CodexChatMergeTarget.OfficialOpenAi,
                                   $"将 {check.Name} 改回 ChatGPT 官方");

        await ExecuteBusyActionAsync(
            $"正在还原 {check.Name} 的默认配置...",
            async () =>
            {
                var result = await _clientApiConfigRestoreService.RestoreAsync(check.Name);
                CodexChatMergeResult? mergeResult = null;
                if (result.Succeeded && shouldMergeChats)
                {
                    mergeResult = await MergeCodexChatsIfRequestedAsync(
                        true,
                        CodexChatMergeTarget.OfficialOpenAi);
                }

                StatusMessage = result.Succeeded
                    ? mergeResult is { Succeeded: false }
                        ? $"默认配置已还原，但聊天整理失败：{mergeResult.Error ?? mergeResult.Summary}"
                        : mergeResult is { Succeeded: true }
                            ? $"{result.Summary}；{mergeResult.Summary}"
                            : result.Summary
                    : $"还原失败：{result.Error ?? result.Summary}";

                var detail = result.ChangedFiles.Count == 0
                    ? $"备份：无\n错误：{result.Error ?? "无"}"
                    : $"已处理：{string.Join("\n", result.ChangedFiles)}\n备份：{string.Join("\n", result.BackupFiles)}\n错误：{result.Error ?? "无"}";

                if (mergeResult is not null)
                {
                    detail += $"\n\n聊天整理：\n{BuildCodexChatMergeDetail(mergeResult)}";
                }

                AppendModuleOutput(
                    $"{check.Name} 默认配置还原",
                    $"{result.Summary}\n聊天整理：{mergeResult?.Summary ?? "未执行"}",
                    detail);

                if (result.Succeeded)
                {
                    await RunClientApiDiagnosticsCoreAsync();
                }
            });
    }

    private static bool IsCodexChatMergeClient(string clientName)
        => clientName is "Codex CLI" or "Codex Desktop" or "VSCode Codex";
}
