using System.Text;
using RelayBench.Core.Models;
using RelayBench.Core.Services;

namespace RelayBench.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly CodexFamilyConfigApplyService _codexFamilyConfigApplyService = new();

    private bool CanApplyRankingRowToCodexApps(ProxyBatchRankingRowViewModel? row)
        => !IsBusy &&
           row is not null &&
           !string.IsNullOrWhiteSpace(row.BaseUrl) &&
           !string.IsNullOrWhiteSpace(row.ApiKey) &&
           !string.IsNullOrWhiteSpace(row.Model);

    private async Task ApplyRankingRowToCodexAppsAsync(ProxyBatchRankingRowViewModel? row)
    {
        if (row is null)
        {
            StatusMessage = "应用失败：没有选中可写入的软件入口。";
            return;
        }

        var confirmed = await ShowConfirmationDialogAsync(
            "确认应用到软件",
            $"确定要将排行榜中的“{row.EntryName}”应用到 Codex 系列软件吗？",
            "本次会写入 Codex CLI / Codex Desktop / VSCode Codex 共用的 .codex 配置：\n" +
            "~/.codex/config.toml\n" +
            "~/.codex/auth.json\n\n" +
            "不会启用本地代理；修改前会自动创建备份。",
            "应用到软件",
            "取消");

        if (!confirmed)
        {
            StatusMessage = $"已取消将 {row.EntryName} 应用到 Codex 软件。";
            return;
        }

        var shouldMergeChats = await ConfirmCodexChatMergeAsync(
            CodexChatMergeTarget.ThirdPartyCustom,
            $"将 {row.EntryName} 应用到 Codex 软件");

        await ExecuteBusyActionAsync(
            $"正在将 {row.EntryName} 应用到 Codex 软件...",
            async () =>
            {
                var result = await _codexFamilyConfigApplyService.ApplyAsync(row.BaseUrl, row.ApiKey, row.Model, row.EntryName);
                CodexChatMergeResult? mergeResult = null;
                if (result.Succeeded)
                {
                    mergeResult = await MergeCodexChatsIfRequestedAsync(
                        shouldMergeChats,
                        CodexChatMergeTarget.ThirdPartyCustom);
                }

                StatusMessage = result.Succeeded
                    ? mergeResult is { Succeeded: false }
                        ? $"配置已应用，但聊天整理失败：{mergeResult.Error ?? mergeResult.Summary}"
                        : mergeResult is { Succeeded: true }
                            ? $"{result.Summary}；{mergeResult.Summary}"
                            : result.Summary
                    : $"应用失败：{result.Error ?? result.Summary}";

                AppendModuleOutput(
                    "排行榜入口应用到软件",
                    BuildRankingRowApplySummary(row, result, mergeResult),
                    BuildRankingRowApplyDetail(row, result, mergeResult));

                if (result.Succeeded)
                {
                    ShowBatchRankingApplyToast(BuildBatchRankingApplyToastMessage(result, mergeResult));
                }
            });
    }

    private static string BuildRankingRowApplySummary(
        ProxyBatchRankingRowViewModel row,
        ClientAppApplyResult result,
        CodexChatMergeResult? mergeResult)
        => $"入口：{row.EntryName}\n目标：Codex CLI / Codex Desktop / VSCode Codex\n配置结果：{result.Summary}\n聊天整理：{mergeResult?.Summary ?? "未执行"}";

    private static string BuildRankingRowApplyDetail(
        ProxyBatchRankingRowViewModel row,
        ClientAppApplyResult result,
        CodexChatMergeResult? mergeResult)
    {
        StringBuilder builder = new();
        builder.AppendLine($"入口名称：{row.EntryName}");
        builder.AppendLine($"BaseUrl：{row.BaseUrl}");
        builder.AppendLine($"API Key：{MaskApiKey(row.ApiKey)}");
        builder.AppendLine($"Model：{row.Model}");
        builder.AppendLine($"应用目标：{string.Join(" / ", result.AppliedTargets)}");
        builder.AppendLine($"已处理文件：{(result.ChangedFiles.Count == 0 ? "无" : string.Join("\n", result.ChangedFiles))}");
        builder.AppendLine($"备份文件：{(result.BackupFiles.Count == 0 ? "无" : string.Join("\n", result.BackupFiles))}");
        builder.AppendLine($"聊天整理：{mergeResult?.Summary ?? "未执行"}");
        if (mergeResult is not null)
        {
            builder.AppendLine(BuildCodexChatMergeDetail(mergeResult));
        }

        builder.Append($"错误：{result.Error ?? "无"}");
        return builder.ToString();
    }

    private static string BuildBatchRankingApplyToastMessage(
        ClientAppApplyResult result,
        CodexChatMergeResult? mergeResult)
        => result.AppliedTargets.Count == 0
            ? "未发现可应用的 Codex 软件"
            : mergeResult is { Succeeded: true }
                ? $"已应用到：{string.Join("、", result.AppliedTargets)}；已合并聊天"
                : $"已应用到：{string.Join("、", result.AppliedTargets)}";

    private void ShowBatchRankingApplyToast(string message)
    {
        _batchRankingApplyToastCancellationSource?.Cancel();
        _batchRankingApplyToastCancellationSource?.Dispose();
        _batchRankingApplyToastCancellationSource = new CancellationTokenSource();

        BatchRankingApplyToastMessage = message;
        IsBatchRankingApplyToastVisible = true;

        _ = HideBatchRankingApplyToastLaterAsync(_batchRankingApplyToastCancellationSource.Token);
    }

    private async Task HideBatchRankingApplyToastLaterAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(1600, cancellationToken);
            IsBatchRankingApplyToastVisible = false;
            await Task.Delay(180, cancellationToken);
            if (!cancellationToken.IsCancellationRequested)
            {
                BatchRankingApplyToastMessage = string.Empty;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
