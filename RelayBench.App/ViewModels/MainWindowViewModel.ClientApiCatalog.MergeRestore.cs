using RelayBench.Core.Models;

namespace RelayBench.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private async Task RestoreClientApiDefaultConfigWithMergeAsync(ClientApiCheck check)
    {
        var confirmed = await ShowConfirmationDialogAsync(
            "确认还原默认配置",
            $"确定要把 {check.Name} 改回默认设置吗？",
            "当前接管配置会被清理，修改前会自动创建备份。",
            "还原默认配置",
            "取消");

        if (!confirmed)
        {
            StatusMessage = $"已取消 {check.Name} 的还原。";
            return;
        }

        var shouldMergeChats = IsCodexChatMergeClient(check.Name) &&
                               await ConfirmCodexChatMergeAsync(
                                   CodexChatMergeTarget.OfficialOpenAi,
                                   $"切回 ChatGPT 官方（{check.Name}）");

        await ExecuteBusyActionAsync(
            $"正在还原 {check.Name}...",
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
                        ? $"默认设置已恢复，但聊天整理失败：{mergeResult.Error ?? mergeResult.Summary}"
                        : mergeResult is { Succeeded: true }
                            ? $"{result.Summary}；{mergeResult.Summary}"
                            : result.Summary
                    : $"还原失败：{result.Error ?? result.Summary}";

                var detail = result.ChangedFiles.Count == 0
                    ? $"备份：无\n错误：{result.Error ?? "无"}"
                    : $"已处理：{string.Join("\n", result.ChangedFiles)}\n备份：{string.Join("\n", result.BackupFiles)}\n错误：{result.Error ?? "无"}";

                if (mergeResult is not null)
                {
                    detail += $"\n\n聊天记录：\n{BuildCodexChatMergeDetail(mergeResult)}";
                }

                AppendModuleOutput(
                    $"{check.Name} 默认配置还原",
                    $"{result.Summary}\n聊天记录：{mergeResult?.Summary ?? "保持原样"}",
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
