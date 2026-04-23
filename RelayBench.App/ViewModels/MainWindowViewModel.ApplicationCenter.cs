using System.Text;
using RelayBench.Core.Models;

namespace RelayBench.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    public string ApplicationCenterApplyTargetSummary
    {
        get
        {
            var missing = GetApplicationCenterMissingContextFields();
            if (missing.Count == 0)
            {
                return "当前接口信息已齐全，可以直接应用到 Codex 系列。";
            }

            return $"还缺 {string.Join("、", missing)}，补齐后再应用。";
        }
    }

    public string ApplicationCenterApplyPreviewDetail
    {
        get
        {
            StringBuilder builder = new();
            builder.AppendLine($"当前地址：{FormatPreviewValue(ProxyBaseUrl)}");
            builder.AppendLine($"当前模型：{FormatPreviewValue(ProxyModel)}");
            builder.AppendLine($"当前密钥：{FormatPreviewApiKey(ProxyApiKey)}");
            builder.AppendLine($"显示名称：{ResolveCurrentProxyDisplayName() ?? "默认名称"}");
            builder.AppendLine();
            builder.AppendLine("将会更新：");
            builder.AppendLine("- ~/.codex/config.toml");
            builder.AppendLine("- ~/.codex/auth.json");
            builder.AppendLine();
            builder.AppendLine("适用软件：");
            builder.AppendLine("- Codex CLI");
            builder.AppendLine("- Codex Desktop");
            builder.AppendLine("- VSCode Codex");
            builder.AppendLine();
            builder.AppendLine("完成后会自动重新扫描本地应用状态。");

            var missing = GetApplicationCenterMissingContextFields();
            if (missing.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine($"待补全：{string.Join("、", missing)}");
            }

            return builder.ToString().TrimEnd();
        }
    }

    private bool CanApplyCurrentInterfaceToCodexApps()
        => !IsBusy && GetApplicationCenterMissingContextFields().Count == 0;

    private async Task ApplyCurrentInterfaceToCodexAppsAsync()
    {
        var missing = GetApplicationCenterMissingContextFields();
        if (missing.Count > 0)
        {
            StatusMessage = $"还缺 {string.Join("、", missing)}，暂时不能应用。";
            return;
        }

        var confirmed = await ShowConfirmationDialogAsync(
            "确认应用到软件",
            "确定要把当前接口应用到 Codex 系列吗？",
            "当前地址、密钥和模型会写入 Codex CLI、Codex Desktop、VSCode Codex 共用配置。\n" +
            "修改前会自动创建备份。",
            "应用到软件",
            "取消");

        if (!confirmed)
        {
            StatusMessage = "已取消本次应用。";
            return;
        }

        var shouldMergeChats = await ConfirmCodexChatMergeAsync(
            CodexChatMergeTarget.ThirdPartyCustom,
            "切到第三方");

        await ExecuteBusyActionAsync(
            "正在应用当前接口...",
            async () =>
            {
                var result = await _codexFamilyConfigApplyService.ApplyAsync(
                    ProxyBaseUrl,
                    ProxyApiKey,
                    ProxyModel,
                    ResolveCurrentProxyDisplayName());

                StringBuilder detailBuilder = new();
                detailBuilder.AppendLine(BuildApplicationCenterApplyDetail(result));

                CodexChatMergeResult? mergeResult = null;
                if (result.Succeeded)
                {
                    mergeResult = await MergeCodexChatsIfRequestedAsync(
                        shouldMergeChats,
                        CodexChatMergeTarget.ThirdPartyCustom,
                        detailBuilder);
                }

                AppendModuleOutput(
                    "应用当前接口到 Codex 系列",
                    BuildApplicationCenterApplySummary(result, mergeResult),
                    detailBuilder.ToString().TrimEnd());

                StatusMessage = result.Succeeded
                    ? mergeResult is { Succeeded: false }
                        ? $"配置已更新，但聊天整理失败：{mergeResult.Error ?? mergeResult.Summary}"
                        : mergeResult is { Succeeded: true }
                            ? $"{result.Summary}；{mergeResult.Summary}"
                            : result.Summary
                    : $"应用失败：{result.Error ?? result.Summary}";

                if (result.Succeeded)
                {
                    await RunClientApiDiagnosticsCoreAsync();
                }
            });
    }

    private static string BuildApplicationCenterApplySummary(
        ClientAppApplyResult result,
        CodexChatMergeResult? mergeResult)
        => $"目标：{(result.AppliedTargets.Count == 0 ? "Codex 系列" : string.Join(" / ", result.AppliedTargets))}\n" +
           $"配置结果：{result.Summary}\n" +
           $"聊天记录：{mergeResult?.Summary ?? "保持原样"}";

    private string BuildApplicationCenterApplyDetail(ClientAppApplyResult result)
    {
        StringBuilder builder = new();
        builder.AppendLine($"当前地址：{ProxyBaseUrl}");
        builder.AppendLine($"当前模型：{ProxyModel}");
        builder.AppendLine($"当前密钥：{MaskApiKey(ProxyApiKey)}");
        builder.AppendLine($"显示名称：{ResolveCurrentProxyDisplayName() ?? "默认名称"}");
        builder.AppendLine($"应用到：{(result.AppliedTargets.Count == 0 ? "无" : string.Join(" / ", result.AppliedTargets))}");
        builder.AppendLine($"更新文件：{(result.ChangedFiles.Count == 0 ? "无" : string.Join("\n", result.ChangedFiles))}");
        builder.AppendLine($"备份文件：{(result.BackupFiles.Count == 0 ? "无" : string.Join("\n", result.BackupFiles))}");
        builder.Append($"错误：{result.Error ?? "无"}");
        return builder.ToString();
    }

    private void NotifyApplicationCenterProxyContextChanged()
    {
        OnPropertyChanged(nameof(ApplicationCenterApplyTargetSummary));
        OnPropertyChanged(nameof(ApplicationCenterApplyPreviewDetail));
        ApplyCurrentInterfaceToCodexAppsCommand?.RaiseCanExecuteChanged();
    }

    private List<string> GetApplicationCenterMissingContextFields()
    {
        List<string> missing = [];

        if (string.IsNullOrWhiteSpace(ProxyBaseUrl))
        {
            missing.Add("地址");
        }

        if (string.IsNullOrWhiteSpace(ProxyModel))
        {
            missing.Add("模型");
        }

        if (string.IsNullOrWhiteSpace(ProxyApiKey))
        {
            missing.Add("密钥");
        }

        return missing;
    }

    private string? ResolveCurrentProxyDisplayName()
    {
        if (Uri.TryCreate(ProxyBaseUrl?.Trim(), UriKind.Absolute, out var uri) &&
            !string.IsNullOrWhiteSpace(uri.Host))
        {
            return uri.Host;
        }

        return null;
    }

    private static string FormatPreviewValue(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? "（未填写）"
            : value.Trim();

    private static string FormatPreviewApiKey(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? "（未填写）"
            : MaskApiKey(value);
}
