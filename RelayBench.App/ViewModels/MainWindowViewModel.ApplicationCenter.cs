using System.Text;
using RelayBench.Core.Models;

namespace RelayBench.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private const string CodexOpenAiProviderDisplayName = "OpenAI";

    public string ApplicationCenterApplyTargetSummary
    {
        get
        {
            var missing = GetApplicationCenterMissingContextFields();
            if (missing.Count == 0)
            {
                return "当前接口信息已齐全；点击应用时会先探测 Chat / Responses / Anthropic 支持，再选择要写入的软件。";
            }

            return $"还缺 {string.Join("、", missing)}，补齐后再应用。";
        }
    }

    public string ApplicationCenterApplyPreviewDetail
    {
        get
        {
            StringBuilder builder = new();
            builder.AppendLine($"当前地址：{FormatPreviewValue(ApplicationCenterBaseUrl)}");
            builder.AppendLine($"当前模型：{FormatPreviewValue(ApplicationCenterModel)}");
            builder.AppendLine($"当前密钥：{FormatPreviewApiKey(ApplicationCenterApiKey)}");
            builder.AppendLine($"显示名称：{ResolveCurrentProxyDisplayName() ?? "默认名称"}");
            builder.AppendLine();
            builder.AppendLine("将会更新：");
            builder.AppendLine("- ~/.codex/config.toml（Codex 系列共用配置）");
            builder.AppendLine("- ~/.codex/auth.json（仅清理旧版 API Key 接管，保留登录态）");
            builder.AppendLine("- ~/.claude/settings.json（仅在选择 Claude CLI 且 Anthropic 可用时）");
            builder.AppendLine();
            builder.AppendLine("适用软件：");
            builder.AppendLine("- Codex CLI");
            builder.AppendLine("- Codex Desktop");
            builder.AppendLine("- VSCode Codex");
            builder.AppendLine("- Claude CLI");
            builder.AppendLine();
            builder.AppendLine("完成后会自动重新扫描本地应用状态。");

            var missing = GetApplicationCenterMissingContextFields();
            if (missing.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine($"待补全：{string.Join("、", missing)}");
            }
            else if (!CanApplyEndpointToCodexApps(ApplicationCenterBaseUrl, ApplicationCenterApiKey, ApplicationCenterModel))
            {
                builder.AppendLine();
                builder.AppendLine("点击应用时会先探测接口格式；Codex 目标优先使用 Responses，必要时回退 OpenAI Chat，Claude 目标需要 Anthropic Messages。");
            }

            return builder.ToString().TrimEnd();
        }
    }

    public string ApplicationCenterProxyApiKeyPreview
        => FormatPreviewApiKey(ApplicationCenterApiKey);

    private bool CanApplyCurrentInterfaceToCodexApps()
        => !IsBusy &&
           GetApplicationCenterMissingContextFields().Count == 0;

    private async Task ApplyCurrentInterfaceToCodexAppsAsync()
    {
        var missing = GetApplicationCenterMissingContextFields();
        if (missing.Count > 0)
        {
            StatusMessage = $"还缺 {string.Join("、", missing)}，暂时不能应用。";
            return;
        }

        var settings = BuildProxySettings(
            ApplicationCenterBaseUrl,
            ApplicationCenterApiKey,
            ApplicationCenterModel);
        var protocolProbeResult = await ProbeEndpointProtocolBeforeApplyAsync(settings);
        if (protocolProbeResult is null)
        {
            return;
        }

        var selectedTargets = await ChooseClientApplyTargetsAsync(
            "应用当前接口到软件",
            settings,
            protocolProbeResult);
        if (selectedTargets.Count == 0)
        {
            StatusMessage = "已取消本次应用。";
            return;
        }

        await ExecuteBusyActionAsync(
            "正在应用当前接口...",
            async () =>
            {
                var cachedApplyInfo = await ResolveCachedCodexApplyInfoAsync(
                    ApplicationCenterBaseUrl,
                    ApplicationCenterApiKey,
                    ApplicationCenterModel);
                var endpoint = new ClientApplyEndpoint(
                    ApplicationCenterBaseUrl,
                    ApplicationCenterApiKey,
                    ApplicationCenterModel,
                    ResolveCurrentProxyDisplayName(),
                    cachedApplyInfo.ContextWindow,
                    cachedApplyInfo.PreferredWireApi);
                var result = await _clientAppConfigApplyService.ApplyAsync(endpoint, selectedTargets);

                CodexChatMergeResult? mergeResult = null;
                if (ShouldAskCodexChatMerge(selectedTargets, result))
                {
                    var shouldMergeChats = await ConfirmCodexChatMergeAsync(
                        CodexChatMergeTarget.ThirdPartyCustom,
                        "切到第三方");
                    mergeResult = await MergeCodexChatsIfRequestedAsync(
                        shouldMergeChats,
                        CodexChatMergeTarget.ThirdPartyCustom);
                }

                AppendModuleOutput(
                    "应用当前接口到软件",
                    BuildClientApplyResultSummary(result, mergeResult),
                    BuildClientApplyResultDetail(
                        "应用当前接口",
                        ApplicationCenterBaseUrl,
                        ApplicationCenterApiKey,
                        ApplicationCenterModel,
                        result,
                        mergeResult));

                StatusMessage = BuildClientApplyStatusMessage(result, mergeResult);

                if (HasSucceededTarget(result))
                {
                    SaveState();
                    await RunClientApiDiagnosticsCoreAsync();
                }
            });
    }

    private void NotifyApplicationCenterProxyContextChanged()
    {
        OnPropertyChanged(nameof(ApplicationCenterApplyTargetSummary));
        OnPropertyChanged(nameof(ApplicationCenterApplyPreviewDetail));
        OnPropertyChanged(nameof(ApplicationCenterProxyApiKeyPreview));
        ApplyCurrentInterfaceToCodexAppsCommand?.RaiseCanExecuteChanged();
    }

    private List<string> GetApplicationCenterMissingContextFields()
    {
        List<string> missing = [];

        if (string.IsNullOrWhiteSpace(ApplicationCenterBaseUrl))
        {
            missing.Add("地址");
        }

        if (string.IsNullOrWhiteSpace(ApplicationCenterModel))
        {
            missing.Add("模型");
        }

        if (string.IsNullOrWhiteSpace(ApplicationCenterApiKey))
        {
            missing.Add("密钥");
        }

        return missing;
    }

    private static string ResolveCurrentProxyDisplayName()
        => CodexOpenAiProviderDisplayName;

    private static string FormatPreviewValue(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? "（未填写）"
            : value.Trim();

    private static string FormatPreviewApiKey(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? "（未填写）"
            : MaskApiKey(value);
}
