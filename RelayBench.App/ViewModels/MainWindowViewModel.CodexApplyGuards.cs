using RelayBench.Core.Models;

namespace RelayBench.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private bool CanApplyEndpointToCodexApps(string? baseUrl, string? apiKey, string? model)
        => IsCodexWireApiCompatible(baseUrl, apiKey, model);

    private static string BuildCodexUnsupportedModelMessage(string? model)
        => $"当前接口的模型“{FormatPreviewValue(model)}”未通过 Codex 需要的 Responses 探测，所以不能直接应用到 Codex。";

    private static string BuildCodexUnsupportedModelMessage(string entryName, string? model)
        => $"“{entryName}”的接口模型“{FormatPreviewValue(model)}”未通过 Codex 需要的 Responses 探测，所以不能直接应用到 Codex。";

    private async Task<bool> ProbeCodexWireApiCompatibilityBeforeApplyAsync(
        ProxyEndpointSettings settings,
        string? entryName = null)
    {
        var result = await ProbeEndpointProtocolBeforeApplyAsync(settings, entryName);
        if (result is null)
        {
            return false;
        }

        if (CanApplyEndpointToCodexApps(settings.BaseUrl, settings.ApiKey, settings.Model))
        {
            return true;
        }

        var message = string.IsNullOrWhiteSpace(entryName)
            ? BuildCodexUnsupportedModelMessage(settings.Model)
            : BuildCodexUnsupportedModelMessage(entryName!, settings.Model);
        StatusMessage = message;
        await ShowCodexWireApiUnsupportedNoticeAsync(message, settings, result);
        return false;
    }

    private async Task<ProxyEndpointProtocolProbeResult?> ProbeEndpointProtocolBeforeApplyAsync(
        ProxyEndpointSettings settings,
        string? entryName = null)
    {
        ProxyEndpointProtocolProbeResult? result = null;
        await ExecuteBusyActionAsync(
            string.IsNullOrWhiteSpace(entryName)
                ? "正在探测当前模型支持的接口格式..."
                : $"正在探测“{entryName}”支持的接口格式...",
            async () => result = await DetectAndCacheProxyWireApiAsync(settings, forceProbe: true));

        StatusMessage = result is null
            ? "接口格式探测未返回完整结果。"
            : BuildProtocolProbeStatusMessage(result);
        return result;
    }

    private Task ShowCodexWireApiUnsupportedNoticeAsync(
        string message,
        ProxyEndpointSettings settings,
        ProxyEndpointProtocolProbeResult? probeResult = null)
        => ShowConfirmationDialogAsync(
            "不能应用到 Codex",
            message,
            $"地址：{FormatPreviewValue(settings.BaseUrl)}\n" +
            $"模型：{FormatPreviewValue(settings.Model)}\n\n" +
            BuildProtocolProbeDetail(probeResult) +
            "\n\n" +
            "原因：Codex 系列客户端只支持 Responses。本次真实探测没有确认 /v1/responses 可用，因此不会写入 Codex 配置。\n\n" +
            "处理方式：换用支持 Responses 的接口，或先通过 RelayBench 本地统一出口提供 Responses 兼容层。",
            "知道了",
            "关闭");

    private static string BuildProtocolProbeDetail(ProxyEndpointProtocolProbeResult? result)
    {
        if (result is null)
        {
            return "格式探测：未拿到完整探测结果。";
        }

        return
            $"格式探测：{result.Summary}\n" +
            $"OpenAI Chat Completions：{FormatSupported(result.ChatCompletionsSupported)}\n" +
            $"OpenAI Responses：{FormatSupported(result.ResponsesSupported)}\n" +
            $"Anthropic Messages：{FormatSupported(result.AnthropicMessagesSupported)}\n" +
            $"推荐写入格式：{(string.IsNullOrWhiteSpace(result.PreferredWireApi) ? "暂无" : result.PreferredWireApi)}" +
            (string.IsNullOrWhiteSpace(result.Error) ? string.Empty : $"\n探测错误：{result.Error}");
    }

    private static string BuildProtocolProbeStatusMessage(ProxyEndpointProtocolProbeResult result)
        => $"格式探测完成：{FormatSupportedLabel("Chat", result.ChatCompletionsSupported)}，" +
           $"{FormatSupportedLabel("Responses", result.ResponsesSupported)}，" +
           $"{FormatSupportedLabel("Anthropic", result.AnthropicMessagesSupported)}。";

    private static string FormatSupported(bool supported)
        => supported ? "可用" : "不可用";

    private static string FormatSupportedLabel(string label, bool supported)
        => $"{label} {(supported ? "可用" : "不可用")}";

    private bool IsCodexWireApiCompatible(string? baseUrl, string? apiKey, string? model)
        => _codexWireApiCompatibilityByEndpointModel.TryGetValue(
            BuildCodexWireApiCompatibilityKey(baseUrl, apiKey, model),
            out var supported) &&
           supported;

    private void RememberCodexWireApiCompatibility(
        string? baseUrl,
        string? apiKey,
        string? model,
        bool wireApiSupported)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) ||
            string.IsNullOrWhiteSpace(apiKey) ||
            string.IsNullOrWhiteSpace(model))
        {
            return;
        }

        _codexWireApiCompatibilityByEndpointModel[BuildCodexWireApiCompatibilityKey(baseUrl, apiKey, model)] =
            wireApiSupported;
        ApplyCurrentInterfaceToCodexAppsCommand?.RaiseCanExecuteChanged();
        ApplyRankingRowToCodexAppsCommand?.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(ApplicationCenterApplyTargetSummary));
        OnPropertyChanged(nameof(ApplicationCenterApplyPreviewDetail));
    }

    private static string BuildCodexWireApiCompatibilityKey(string? baseUrl, string? apiKey, string? model)
    {
        var normalizedBaseUrl = (baseUrl ?? string.Empty).Trim().TrimEnd('/').ToLowerInvariant();
        var normalizedApiKey = (apiKey ?? string.Empty).Trim();
        var normalizedModel = (model ?? string.Empty).Trim().ToLowerInvariant();
        return string.Join('\n', normalizedBaseUrl, normalizedApiKey, normalizedModel);
    }
}
