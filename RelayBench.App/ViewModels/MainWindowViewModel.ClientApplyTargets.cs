using System.Text;
using RelayBench.App.Services;
using RelayBench.Core.Models;
using RelayBench.Core.Services;

namespace RelayBench.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly ClientAppApplyPlanner _clientAppApplyPlanner = new();
    private readonly ClientAppConfigApplyService _clientAppConfigApplyService = new();

    private IReadOnlyList<ClientApplyTarget> BuildClientApplyTargets(
        ProxyEndpointSettings settings,
        ProxyEndpointProtocolProbeResult protocolProbeResult)
    {
        var installedNames = _lastClientApiDiagnosticsResult?.Checks
            .Where(check => check.Installed)
            .Select(check => check.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return _clientAppApplyPlanner.BuildTargets(new ClientAppApplyPlanContext(
            settings.BaseUrl,
            settings.ApiKey,
            settings.Model,
            protocolProbeResult.ResponsesSupported,
            protocolProbeResult.ChatCompletionsSupported,
            protocolProbeResult.AnthropicMessagesSupported,
            installedNames));
    }

    private async Task<IReadOnlyList<ClientApplyTargetSelection>> ChooseClientApplyTargetsAsync(
        string title,
        ProxyEndpointSettings settings,
        ProxyEndpointProtocolProbeResult protocolProbeResult)
    {
        var targets = BuildClientApplyTargets(settings, protocolProbeResult);
        var summary =
            BuildProtocolProbeDetail(protocolProbeResult) +
            "\n\n请选择本次要写入配置的软件。本次结果来自真实强制探测：Codex 系列只在 Responses 可用时开放；Claude CLI 可直连 Anthropic，或在仅 OpenAI Chat 可用时通过 RelayBench 本地统一出口转换。";
        return await ShowClientApplyTargetDialogAsync(title, summary, targets);
    }

    private static bool HasSucceededCodexTarget(ClientAppApplyResult result)
        => result.TargetResults.Any(target =>
            target.Protocol == ClientApplyProtocolKind.Responses &&
            target.Succeeded);

    private static bool ShouldAskCodexChatMerge(
        IReadOnlyList<ClientApplyTargetSelection> selectedTargets,
        ClientAppApplyResult result)
        => selectedTargets.Any(target => target.Protocol == ClientApplyProtocolKind.Responses) &&
           HasSucceededCodexTarget(result);

    private static bool HasSucceededTarget(ClientAppApplyResult result)
        => result.TargetResults.Any(target => target.Succeeded) || result.AppliedTargets.Count > 0;

    private static string BuildClientApplyResultSummary(
        ClientAppApplyResult result,
        CodexChatMergeResult? mergeResult)
    {
        StringBuilder builder = new();
        builder.AppendLine(result.Summary);
        if (result.TargetResults.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("逐软件结果：");
            foreach (var target in result.TargetResults)
            {
                builder.AppendLine(target.Succeeded
                    ? $"- {target.DisplayName}：成功"
                    : $"- {target.DisplayName}：失败（{target.Error ?? "未知错误"}）");
            }
        }

        builder.Append($"聊天记录：{mergeResult?.Summary ?? "保持原样"}");
        return builder.ToString();
    }

    private static string BuildClientApplyResultDetail(
        string sourceTitle,
        string baseUrl,
        string apiKey,
        string model,
        ClientAppApplyResult result,
        CodexChatMergeResult? mergeResult)
    {
        StringBuilder builder = new();
        builder.AppendLine(sourceTitle);
        builder.AppendLine($"地址：{baseUrl}");
        builder.AppendLine($"密钥：{MaskApiKey(apiKey)}");
        builder.AppendLine($"模型：{model}");
        builder.AppendLine();
        builder.AppendLine("逐软件结果：");
        if (result.TargetResults.Count == 0)
        {
            builder.AppendLine("无");
        }
        else
        {
            foreach (var target in result.TargetResults)
            {
                builder.AppendLine(target.Succeeded
                    ? $"- {target.DisplayName} [{FormatClientApplyProtocol(target.Protocol)}]：成功"
                    : $"- {target.DisplayName} [{FormatClientApplyProtocol(target.Protocol)}]：失败（{target.Error ?? "未知错误"}）");
            }
        }

        builder.AppendLine();
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

    private static string FormatClientApplyProtocol(ClientApplyProtocolKind protocol)
        => protocol switch
        {
            ClientApplyProtocolKind.Responses => "Responses",
            ClientApplyProtocolKind.OpenAiCompatible => "OpenAI 兼容",
            ClientApplyProtocolKind.Anthropic => "Anthropic",
            _ => protocol.ToString()
        };

    private static string BuildClientApplyStatusMessage(
        ClientAppApplyResult result,
        CodexChatMergeResult? mergeResult)
    {
        var hasSucceededTarget = HasSucceededTarget(result);
        if (!hasSucceededTarget)
        {
            return $"应用失败：{result.Error ?? result.Summary}";
        }

        return result.Succeeded
            ? mergeResult is { Succeeded: false }
                ? $"配置已更新，但聊天整理失败：{mergeResult.Error ?? mergeResult.Summary}"
                : mergeResult is { Succeeded: true }
                    ? $"{result.Summary}；{mergeResult.Summary}"
                    : result.Summary
            : result.Summary;
    }

    private async Task<ClientApplyEndpoint?> PrepareClaudeRelayEndpointIfNeededAsync(
        ProxyEndpointSettings settings,
        ProxyEndpointProtocolProbeResult protocolProbeResult,
        IReadOnlyList<ClientApplyTargetSelection> selectedTargets,
        string sourceName)
    {
        if (!RequiresClaudeRelayEndpoint(protocolProbeResult, selectedTargets))
        {
            return null;
        }

        EnsureTransparentProxyRouteForClientApply(settings, protocolProbeResult, sourceName);
        if (IsTransparentProxyRunning)
        {
            _transparentProxyService.UpdateRoutes(BuildTransparentProxyRuntimeRoutes());
        }
        else
        {
            await StartTransparentProxyCoreAsync(isAutoStart: false);
        }

        return BuildClaudeRelayEndpoint(settings.Model);
    }

    private ClientApplyEndpoint? BuildLocalClaudeEndpointIfSelected(
        string model,
        IReadOnlyList<ClientApplyTargetSelection> selectedTargets)
        => selectedTargets.Any(IsClaudeCliSelection)
            ? BuildClaudeRelayEndpoint(model)
            : null;

    private ClientApplyEndpoint BuildClaudeRelayEndpoint(string model)
        => new(
            BuildTransparentProxyAnthropicBaseUrl(),
            "relaybench-local",
            model,
            "RelayBench 本地统一出口（Chat 转 Anthropic）",
            null,
            ProxyWireApiProbeService.AnthropicMessagesWireApi);

    private static bool RequiresClaudeRelayEndpoint(
        ProxyEndpointProtocolProbeResult protocolProbeResult,
        IReadOnlyList<ClientApplyTargetSelection> selectedTargets)
        => selectedTargets.Any(IsClaudeCliSelection) &&
           !protocolProbeResult.AnthropicMessagesSupported &&
           protocolProbeResult.ChatCompletionsSupported;

    private void EnsureTransparentProxyRouteForClientApply(
        ProxyEndpointSettings settings,
        ProxyEndpointProtocolProbeResult protocolProbeResult,
        string sourceName)
    {
        var name = BuildClientApplyRelayRouteName(sourceName, settings.Model);
        var item = TransparentProxyRouteEditorItems.FirstOrDefault(route =>
            !route.IsCodexOAuthAuth &&
            string.Equals(NormalizeEndpointForRouteMatch(route.BaseUrl), NormalizeEndpointForRouteMatch(settings.BaseUrl), StringComparison.OrdinalIgnoreCase) &&
            route.Models.Any(model => string.Equals(model, settings.Model, StringComparison.OrdinalIgnoreCase)));
        if (item is null)
        {
            item = new TransparentProxyRouteEditorItemViewModel();
            AttachTransparentProxyRouteEditorItem(item);
            TransparentProxyRouteEditorItems.Add(item);
        }

        _isRefreshingTransparentProxyRouteEditor = true;
        try
        {
            item.IsEnabled = true;
            item.Name = name;
            item.BaseUrl = settings.BaseUrl;
            item.ApiKey = settings.ApiKey;
            item.AuthMode = TransparentProxyRouteAuthModes.ApiKey;
            item.PriorityText = "0";
            item.ModelsText = settings.Model;
            SelectedTransparentProxyRouteEditorItem = item;
        }
        finally
        {
            _isRefreshingTransparentProxyRouteEditor = false;
        }

        UpdateTransparentProxyRoutesTextFromEditor();

        var routeId = TransparentProxyRouteTextCodec.BuildRouteId(item.Name, item.BaseUrl, item.Prefix);
        _transparentProxyProtocolSnapshots[routeId] = new TransparentProxyProtocolDiscoverySnapshot(
            protocolProbeResult.PreferredWireApi,
            protocolProbeResult.ChatCompletionsSupported,
            protocolProbeResult.ResponsesSupported,
            protocolProbeResult.AnthropicMessagesSupported,
            protocolProbeResult.CheckedAt,
            WasProbed: true);
        RefreshTransparentProxyRoutePreview();
    }

    private string BuildTransparentProxyAnthropicBaseUrl()
        => $"http://127.0.0.1:{ParseTransparentProxyPort()}";

    private static bool IsClaudeCliSelection(ClientApplyTargetSelection target)
        => string.Equals(target.TargetId, "claude-cli", StringComparison.OrdinalIgnoreCase) &&
           target.Protocol == ClientApplyProtocolKind.Anthropic;

    private static string BuildClientApplyRelayRouteName(string sourceName, string model)
    {
        var normalizedSource = string.IsNullOrWhiteSpace(sourceName) ? "当前接口" : sourceName.Trim();
        var normalizedModel = string.IsNullOrWhiteSpace(model) ? "默认模型" : model.Trim();
        return $"应用接入：{normalizedSource} {normalizedModel}";
    }

    private static string NormalizeEndpointForRouteMatch(string? value)
        => (value ?? string.Empty).Trim().TrimEnd('/');
}
