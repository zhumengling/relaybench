using RelayBench.Core.Models;

namespace RelayBench.Core.Services;

public sealed partial class ProxyDiagnosticsService
{
    public async Task<ProxyEndpointProtocolProbeResult> ProbeProtocolAsync(
        ProxyEndpointSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (!TryValidateSettings(settings, out var normalizedSettings, out var baseUri, out var error))
        {
            return new ProxyEndpointProtocolProbeResult(
                DateTimeOffset.Now,
                settings.BaseUrl,
                settings.Model,
                false,
                false,
                null,
                "协议探测失败：接口配置无效。",
                error);
        }

        if (string.IsNullOrWhiteSpace(normalizedSettings.Model))
        {
            return new ProxyEndpointProtocolProbeResult(
                DateTimeOffset.Now,
                baseUri.ToString(),
                string.Empty,
                false,
                false,
                null,
                "协议探测失败：缺少模型名称。",
                "请先填写模型名称，再检测链接方式。");
        }

        using var client = CreateClient(baseUri, normalizedSettings);
        try
        {
            var outcome = await ProbeEndpointWireApiAsync(
                client,
                baseUri,
                normalizedSettings.Model,
                [normalizedSettings.Model],
                cancellationToken);
            if (outcome is null)
            {
                return new ProxyEndpointProtocolProbeResult(
                    DateTimeOffset.Now,
                    baseUri.ToString(),
                    normalizedSettings.Model,
                    false,
                    false,
                    null,
                    "协议探测失败：没有可用于探测的模型。",
                    "缺少模型名称。");
            }

            return new ProxyEndpointProtocolProbeResult(
                DateTimeOffset.Now,
                baseUri.ToString(),
                outcome.ProbeModel,
                outcome.ChatCompletionsSupported,
                outcome.ResponsesSupported,
                outcome.PreferredWireApi,
                outcome.Summary,
                outcome.ChatCompletionsSupported || outcome.ResponsesSupported
                    ? null
                    : "chat/completions 与 responses 均未通过。");
        }
        catch (Exception ex) when (!IsCancellationRequestedException(ex, cancellationToken))
        {
            return new ProxyEndpointProtocolProbeResult(
                DateTimeOffset.Now,
                baseUri.ToString(),
                normalizedSettings.Model,
                false,
                false,
                null,
                $"协议探测失败：{ex.Message}",
                ex.Message);
        }
    }

    private static async Task<ProtocolWireProbeOutcome?> ProbeEndpointWireApiAsync(
        HttpClient client,
        Uri baseUri,
        string requestedModel,
        IReadOnlyList<string> sampleModels,
        CancellationToken cancellationToken)
    {
        var probeModel = SelectProtocolProbeModel(requestedModel, sampleModels);
        if (string.IsNullOrWhiteSpace(probeModel))
        {
            return null;
        }

        var chatPath = BuildApiPath(baseUri, "chat/completions");
        var responsesPath = BuildApiPath(baseUri, "responses");
        var chatProbe = await ProbeJsonScenarioAsync(
            client,
            chatPath,
            BuildChatPayload(probeModel, stream: false),
            ProxyProbeScenarioKind.ChatCompletions,
            "普通对话",
            ParseChatPreview,
            cancellationToken);
        var responsesProbe = await ProbeJsonScenarioAsync(
            client,
            responsesPath,
            BuildResponsesPayload(probeModel),
            ProxyProbeScenarioKind.Responses,
            "Responses",
            ParseResponsesPreview,
            cancellationToken);

        var chatSupported = chatProbe.ScenarioResult.Success;
        var responsesSupported = responsesProbe.ScenarioResult.Success;
        var preferredWireApi = ResolvePreferredWireApi(
            baseUri.ToString(),
            probeModel,
            chatSupported,
            responsesSupported);
        var summary = BuildProtocolProbeSummary(probeModel, chatSupported, responsesSupported, preferredWireApi);

        return new ProtocolWireProbeOutcome(
            probeModel,
            chatSupported,
            responsesSupported,
            preferredWireApi,
            summary);
    }

    private static string SelectProtocolProbeModel(string requestedModel, IReadOnlyList<string> sampleModels)
    {
        var normalizedRequested = requestedModel.Trim();
        if (!string.IsNullOrWhiteSpace(normalizedRequested) &&
            sampleModels.Contains(normalizedRequested, StringComparer.OrdinalIgnoreCase))
        {
            return normalizedRequested;
        }

        var likelyChatModel = sampleModels.FirstOrDefault(static model => !LooksLikeNonChatModel(model));
        return likelyChatModel ?? sampleModels.FirstOrDefault() ?? normalizedRequested;
    }

    private static string? ResolvePreferredWireApi(
        string baseUrl,
        string model,
        bool chatSupported,
        bool responsesSupported)
    {
        if (chatSupported && !responsesSupported)
        {
            return "chat";
        }

        if (responsesSupported && !chatSupported)
        {
            return "responses";
        }

        if (chatSupported && responsesSupported)
        {
            return CodexFamilyConfigApplyService.ResolveCodexWireApiPreference(baseUrl, model);
        }

        return null;
    }

    private static string BuildProtocolProbeSummary(
        string model,
        bool chatSupported,
        bool responsesSupported,
        string? preferredWireApi)
    {
        var chatText = chatSupported ? "chat 可用" : "chat 不可用";
        var responsesText = responsesSupported ? "responses 可用" : "responses 不可用";
        var preferredText = string.IsNullOrWhiteSpace(preferredWireApi)
            ? "暂未判断写入协议"
            : $"建议写入 wire_api={preferredWireApi}";
        return $"协议探测模型：{model}；{chatText}，{responsesText}；{preferredText}。";
    }

    private static bool LooksLikeNonChatModel(string? model)
    {
        var normalized = model?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return true;
        }

        return normalized.Contains("embedding", StringComparison.Ordinal) ||
               normalized.Contains("rerank", StringComparison.Ordinal) ||
               normalized.Contains("moderation", StringComparison.Ordinal) ||
               normalized.Contains("whisper", StringComparison.Ordinal) ||
               normalized.Contains("transcribe", StringComparison.Ordinal) ||
               normalized.Contains("transcription", StringComparison.Ordinal) ||
               normalized.StartsWith("tts-", StringComparison.Ordinal) ||
               normalized.Contains("-tts", StringComparison.Ordinal) ||
               normalized.Contains("text-to-speech", StringComparison.Ordinal) ||
               normalized.Contains("dall-e", StringComparison.Ordinal) ||
               normalized.Contains("gpt-image", StringComparison.Ordinal) ||
               normalized.Contains("stable-diffusion", StringComparison.Ordinal) ||
               normalized.Contains("sdxl", StringComparison.Ordinal) ||
               normalized.Contains("imagen", StringComparison.Ordinal) ||
               normalized.Contains("flux", StringComparison.Ordinal);
    }
}
