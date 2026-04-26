using System.Text;
using RelayBench.Core.Models;
using RelayBench.Core.Services;

namespace RelayBench.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly CodexChatMergeService _codexChatMergeService = new();

    private async Task<CodexChatMergeResult?> MergeCodexChatsIfRequestedAsync(
        bool shouldMerge,
        CodexChatMergeTarget target,
        string? targetModel = null,
        StringBuilder? detailBuilder = null)
    {
        if (!shouldMerge)
        {
            detailBuilder?.AppendLine("聊天记录：保持原样");
            return null;
        }

        var mergeResult = await _codexChatMergeService.MergeAsync(target, targetModel);
        detailBuilder?.AppendLine("聊天记录：");
        detailBuilder?.AppendLine(BuildCodexChatMergeDetail(mergeResult));
        return mergeResult;
    }

    private static string BuildCodexChatMergeDetail(CodexChatMergeResult result)
    {
        StringBuilder builder = new();
        builder.AppendLine($"显示位置：{CodexChatMergeService.BuildTargetDisplayName(result.Target)}");
        builder.AppendLine($"结果：{result.Summary}");
        builder.AppendLine($"整理聊天数：{result.RebuckettedThreadCount}");
        builder.AppendLine($"同步记录文件：{result.UpdatedSessionFileCount}");
        builder.AppendLine($"已处理文件：{(result.ChangedFiles.Count == 0 ? "无" : string.Join("\n", result.ChangedFiles))}");
        builder.AppendLine($"备份文件：{(result.BackupFiles.Count == 0 ? "无" : string.Join("\n", result.BackupFiles))}");
        builder.AppendLine($"提示：{result.Warning ?? "无"}");
        builder.Append($"错误：{result.Error ?? "无"}");
        return builder.ToString();
    }
}
