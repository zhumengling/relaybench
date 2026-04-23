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
                return "接口已就绪，可写入 Codex CLI / Desktop / VSCode Codex；写入前会自动备份。";
            }

            return $"还缺 {string.Join("、", missing)}，补齐后可写入 Codex 系列。";
        }
    }

    public string ApplicationCenterApplyPreviewDetail
    {
        get
        {
            StringBuilder builder = new();
            builder.AppendLine($"当前 Base URL：{FormatPreviewValue(ProxyBaseUrl)}");
            builder.AppendLine($"当前模型：{FormatPreviewValue(ProxyModel)}");
            builder.AppendLine($"当前 API Key：{FormatPreviewApiKey(ProxyApiKey)}");
            builder.AppendLine($"配置名称：{ResolveCurrentProxyDisplayName() ?? "将使用默认 Custom OpenAI-Compatible"}");
            builder.AppendLine();
            builder.AppendLine("写入目标：");
            builder.AppendLine("- ~/.codex/config.toml");
            builder.AppendLine("- ~/.codex/auth.json");
            builder.AppendLine("- ~/.codex/settings.json（仅在还原 / 基线场景中用到）");
            builder.AppendLine();
            builder.AppendLine("适用软件：");
            builder.AppendLine("- Codex CLI");
            builder.AppendLine("- Codex Desktop");
            builder.AppendLine("- VSCode Codex");
            builder.AppendLine();
            builder.AppendLine("说明：");
            builder.AppendLine("- 不会启用或修改本地代理");
            builder.AppendLine("- 不会动 Claude CLI / Antigravity 的配置");
            builder.AppendLine("- 完成写入后会立即重新扫描本地应用状态");

            var missing = GetApplicationCenterMissingContextFields();
            if (missing.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine($"待补全项：{string.Join("、", missing)}");
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
            StatusMessage = $"应用失败：还缺 {string.Join("、", missing)}。";
            return;
        }

        var confirmed = await ShowConfirmationDialogAsync(
            "确认应用到软件",
            "确定要将当前接口应用到 Codex 系列软件吗？",
            "本次会写入 Codex CLI / Codex Desktop / VSCode Codex 共用的 .codex 配置。\n" +
            "修改前会自动创建备份。",
            "应用到软件",
            "取消");

        if (!confirmed)
        {
            StatusMessage = "已取消将当前接口应用到 Codex 系列。";
            return;
        }

        var shouldMergeChats = await ConfirmCodexChatMergeAsync(
            CodexChatMergeTarget.ThirdPartyCustom,
            "应用到 Codex 系列");

        await ExecuteBusyActionAsync(
            "正在应用当前接口到 Codex 系列...",
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
                        ? $"配置已应用，但聊天整理失败：{mergeResult.Error ?? mergeResult.Summary}"
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
           $"聊天整理：{mergeResult?.Summary ?? "未执行"}";

    private string BuildApplicationCenterApplyDetail(ClientAppApplyResult result)
    {
        StringBuilder builder = new();
        builder.AppendLine($"当前 Base URL：{ProxyBaseUrl}");
        builder.AppendLine($"当前模型：{ProxyModel}");
        builder.AppendLine($"当前 API Key：{MaskApiKey(ProxyApiKey)}");
        builder.AppendLine($"配置名称：{ResolveCurrentProxyDisplayName() ?? "Custom OpenAI-Compatible"}");
        builder.AppendLine($"应用目标：{(result.AppliedTargets.Count == 0 ? "无" : string.Join(" / ", result.AppliedTargets))}");
        builder.AppendLine($"已处理文件：{(result.ChangedFiles.Count == 0 ? "无" : string.Join("\n", result.ChangedFiles))}");
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
            missing.Add("Base URL");
        }

        if (string.IsNullOrWhiteSpace(ProxyModel))
        {
            missing.Add("模型");
        }

        if (string.IsNullOrWhiteSpace(ProxyApiKey))
        {
            missing.Add("API Key");
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
