using RelayBench.Core.Models;

namespace RelayBench.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private bool CanApplyEndpointToCodexApps(string? baseUrl, string? apiKey, string? model)
        => IsCodexResponsesCompatible(baseUrl, apiKey, model);

    private static string BuildCodexUnsupportedModelMessage(string? model)
        => $"当前接口的模型“{FormatPreviewValue(model)}”不支持 Codex 需要的 Responses API，所以不能应用到 Codex。";

    private static string BuildCodexUnsupportedModelMessage(string entryName, string? model)
        => $"“{entryName}”的接口模型“{FormatPreviewValue(model)}”不支持 Codex 需要的 Responses API，所以不能应用到 Codex。";

    private async Task<bool> ProbeCodexResponsesCompatibilityBeforeApplyAsync(
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
        await ShowCodexResponsesUnsupportedNoticeAsync(message, settings, result);
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

    private Task ShowCodexResponsesUnsupportedNoticeAsync(
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
            "原因：刚才的接口探测没有确认 /v1/responses 可用。Codex 当前配置只写入 wire_api = \"responses\"，" +
            "如果接口不支持 Responses API，应用后会导致 Codex 不能正常请求。\n\n" +
            "处理方式：换用支持 Responses API 的接口，或先在中转/本地服务里开启 Responses 兼容。",
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

    private bool IsCodexResponsesCompatible(string? baseUrl, string? apiKey, string? model)
        => _codexResponsesCompatibilityByEndpointModel.TryGetValue(
            BuildCodexResponsesCompatibilityKey(baseUrl, apiKey, model),
            out var supported) &&
           supported;

    private void RememberCodexResponsesCompatibility(
        string? baseUrl,
        string? apiKey,
        string? model,
        bool responsesSupported)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) ||
            string.IsNullOrWhiteSpace(apiKey) ||
            string.IsNullOrWhiteSpace(model))
        {
            return;
        }

        _codexResponsesCompatibilityByEndpointModel[BuildCodexResponsesCompatibilityKey(baseUrl, apiKey, model)] =
            responsesSupported;
        ApplyCurrentInterfaceToCodexAppsCommand?.RaiseCanExecuteChanged();
        ApplyRankingRowToCodexAppsCommand?.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(ApplicationCenterApplyTargetSummary));
        OnPropertyChanged(nameof(ApplicationCenterApplyPreviewDetail));
    }

    private static string BuildCodexResponsesCompatibilityKey(string? baseUrl, string? apiKey, string? model)
    {
        var normalizedBaseUrl = (baseUrl ?? string.Empty).Trim().TrimEnd('/').ToLowerInvariant();
        var normalizedApiKey = (apiKey ?? string.Empty).Trim();
        var normalizedModel = (model ?? string.Empty).Trim().ToLowerInvariant();
        return string.Join('\n', normalizedBaseUrl, normalizedApiKey, normalizedModel);
    }
}
