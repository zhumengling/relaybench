using System.Text;
using System.Windows;
using NetTest.Core.Models;
using NetTest.Core.Services;

namespace NetTest.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly CodexFamilyConfigApplyService _codexFamilyConfigApplyService = new();

    private bool CanApplyRankingRowToCodexApps(ProxyBatchRankingRowViewModel? row)
        => !IsBusy &&
           row is not null &&
           !string.IsNullOrWhiteSpace(row.BaseUrl) &&
           !string.IsNullOrWhiteSpace(row.ApiKey) &&
           !string.IsNullOrWhiteSpace(row.Model);

    private Task ApplyRankingRowToCodexAppsAsync(ProxyBatchRankingRowViewModel? row)
    {
        if (row is null)
        {
            StatusMessage = "应用失败：没有选中可写入的软件入口。";
            return Task.CompletedTask;
        }

        var confirmed = MessageBox.Show(
            $"确定要将排行榜中的“{row.EntryName}”应用到 Codex 系列软件吗？\n\n" +
            "本次会写入 Codex CLI / Codex Desktop / VSCode Codex 共用的 .codex 配置：\n" +
            "~/.codex/config.toml\n" +
            "~/.codex/auth.json\n\n" +
            "不会启用本地代理；修改前会自动创建备份。",
            "确认应用到软件",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);

        if (confirmed != MessageBoxResult.Yes)
        {
            StatusMessage = $"已取消将 {row.EntryName} 应用到 Codex 软件。";
            return Task.CompletedTask;
        }

        return ExecuteBusyActionAsync(
            $"正在将 {row.EntryName} 应用到 Codex 软件...",
            async () =>
            {
                var result = await _codexFamilyConfigApplyService.ApplyAsync(row.BaseUrl, row.ApiKey, row.Model, row.EntryName);
                StatusMessage = result.Succeeded
                    ? result.Summary
                    : $"应用失败：{result.Error ?? result.Summary}";

                AppendModuleOutput(
                    "排行榜入口应用到软件",
                    BuildRankingRowApplySummary(row, result),
                    BuildRankingRowApplyDetail(row, result));

                if (result.Succeeded)
                {
                    ShowBatchRankingApplyToast(BuildBatchRankingApplyToastMessage(result));
                }
            });
    }

    private static string BuildRankingRowApplySummary(ProxyBatchRankingRowViewModel row, ClientAppApplyResult result)
        => $"入口：{row.EntryName}\n目标：Codex CLI / Codex Desktop / VSCode Codex\n结果：{result.Summary}";

    private static string BuildRankingRowApplyDetail(ProxyBatchRankingRowViewModel row, ClientAppApplyResult result)
    {
        StringBuilder builder = new();
        builder.AppendLine($"入口名称：{row.EntryName}");
        builder.AppendLine($"BaseUrl：{row.BaseUrl}");
        builder.AppendLine($"API Key：{MaskApiKey(row.ApiKey)}");
        builder.AppendLine($"Model：{row.Model}");
        builder.AppendLine($"应用目标：{string.Join(" / ", result.AppliedTargets)}");
        builder.AppendLine($"已处理文件：{(result.ChangedFiles.Count == 0 ? "无" : string.Join("\n", result.ChangedFiles))}");
        builder.AppendLine($"备份文件：{(result.BackupFiles.Count == 0 ? "无" : string.Join("\n", result.BackupFiles))}");
        builder.Append($"错误：{result.Error ?? "无"}");
        return builder.ToString();
    }

    private static string BuildBatchRankingApplyToastMessage(ClientAppApplyResult result)
        => result.AppliedTargets.Count == 0
            ? "未发现可应用的 Codex 软件"
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
