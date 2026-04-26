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
            StatusMessage = "没有可应用的软件入口。";
            return;
        }

        var confirmed = await ShowConfirmationDialogAsync(
            "确认应用到软件",
            $"确定要把“{row.EntryName}”应用到 Codex 系列吗？",
            "当前地址、密钥和模型会写入 Codex CLI、Codex Desktop、VSCode Codex 共用配置。\n" +
            "修改前会自动创建备份。",
            "应用到软件",
            "取消");

        if (!confirmed)
        {
            StatusMessage = $"已取消“{row.EntryName}”的应用。";
            return;
        }

        var shouldMergeChats = await ConfirmCodexChatMergeAsync(
            CodexChatMergeTarget.ThirdPartyCustom,
            $"切到第三方（{row.EntryName}）");

        await ExecuteBusyActionAsync(
            $"正在应用“{row.EntryName}”...",
            async () =>
            {
                var settings = BuildProxySettings(row.BaseUrl, row.ApiKey, row.Model);
                await DetectAndCacheProxyWireApiAsync(settings);
                var cachedApplyInfo = await ResolveCachedCodexApplyInfoAsync(
                    row.BaseUrl,
                    row.ApiKey,
                    row.Model);
                var result = await _codexFamilyConfigApplyService.ApplyAsync(
                    row.BaseUrl,
                    row.ApiKey,
                    row.Model,
                    CodexOpenAiProviderDisplayName,
                    cachedApplyInfo.ContextWindow,
                    cachedApplyInfo.PreferredWireApi);
                CodexChatMergeResult? mergeResult = null;
                if (result.Succeeded)
                {
                    mergeResult = await MergeCodexChatsIfRequestedAsync(
                        shouldMergeChats,
                        CodexChatMergeTarget.ThirdPartyCustom,
                        row.Model);
                }

                StatusMessage = result.Succeeded
                    ? mergeResult is { Succeeded: false }
                        ? $"配置已更新，但聊天整理失败：{mergeResult.Error ?? mergeResult.Summary}"
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
        => $"入口：{row.EntryName}\n目标：Codex CLI / Codex Desktop / VSCode Codex\n配置结果：{result.Summary}\n聊天记录：{mergeResult?.Summary ?? "保持原样"}";

    private static string BuildRankingRowApplyDetail(
        ProxyBatchRankingRowViewModel row,
        ClientAppApplyResult result,
        CodexChatMergeResult? mergeResult)
    {
        StringBuilder builder = new();
        builder.AppendLine($"入口名称：{row.EntryName}");
        builder.AppendLine($"地址：{row.BaseUrl}");
        builder.AppendLine($"密钥：{MaskApiKey(row.ApiKey)}");
        builder.AppendLine($"模型：{row.Model}");
        builder.AppendLine($"应用到：{string.Join(" / ", result.AppliedTargets)}");
        builder.AppendLine($"更新文件：{(result.ChangedFiles.Count == 0 ? "无" : string.Join("\n", result.ChangedFiles))}");
        builder.AppendLine($"备份文件：{(result.BackupFiles.Count == 0 ? "无" : string.Join("\n", result.BackupFiles))}");
        builder.AppendLine($"聊天记录：{mergeResult?.Summary ?? "保持原样"}");
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
                ? $"已应用到：{string.Join("、", result.AppliedTargets)}；聊天已整理"
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
