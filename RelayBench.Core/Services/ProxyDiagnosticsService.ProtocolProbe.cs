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
                outcome.AnthropicMessagesSupported,
                outcome.PreferredWireApi,
                outcome.Summary,
                outcome.ChatCompletionsSupported || outcome.ResponsesSupported || outcome.AnthropicMessagesSupported
                    ? null
                    : "chat/completions、responses 与 Anthropic messages 均未通过。");
        }
        catch (Exception ex) when (!IsCancellationRequestedException(ex, cancellationToken))
        {
            return new ProxyEndpointProtocolProbeResult(
                DateTimeOffset.Now,
                baseUri.ToString(),
                normalizedSettings.Model,
                false,
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
        var anthropicPath = BuildApiPath(baseUri, "messages");
        var anthropicProbe = await ProbeAnthropicMessagesScenarioAsync(
            client,
            anthropicPath,
            BuildAnthropicMessagesPayload(probeModel),
            cancellationToken);

        var chatSupported = chatProbe.ScenarioResult.Success &&
                            IsModelEligibleForChatCompletions(probeModel);
        var responsesSupported = responsesProbe.ScenarioResult.Success &&
                                 IsModelEligibleForResponses(probeModel);
        var anthropicSupported = anthropicProbe.ScenarioResult.Success &&
                                 IsModelEligibleForAnthropicMessages(probeModel);
        var preferredWireApi = ResolvePreferredWireApi(
            baseUri.ToString(),
            probeModel,
            chatSupported,
            responsesSupported);
        var summary = BuildProtocolProbeSummary(
            probeModel,
            chatSupported,
            responsesSupported,
            anthropicSupported,
            preferredWireApi);

        return new ProtocolWireProbeOutcome(
            probeModel,
            chatSupported,
            responsesSupported,
            anthropicSupported,
            preferredWireApi,
            summary);
    }

    private static async Task<JsonProbeOutcome> ProbeAnthropicMessagesScenarioAsync(
        HttpClient client,
        string path,
        string payload,
        CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json")
            };
            request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            var apiKey = client.DefaultRequestHeaders.Authorization?.Parameter;
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
            }

            using var response = await client.SendAsync(request, cancellationToken);
            var statusCode = (int)response.StatusCode;
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            stopwatch.Stop();
            var headers = ExtractInterestingHeaders(response);
            var requestId = ExtractRequestId(headers);
            var traceId = ExtractTraceId(headers);

            if (!response.IsSuccessStatusCode)
            {
                var failureKind = ClassifyResponseFailure(ProxyProbeScenarioKind.ChatCompletions, statusCode, content);
                var bodySample = ExtractBodySample(content);
                return new JsonProbeOutcome(
                    new ProxyProbeScenarioResult(
                        ProxyProbeScenarioKind.ChatCompletions,
                        "Anthropic Messages",
                        DescribeCapability(failureKind, false),
                        false,
                        statusCode,
                        stopwatch.Elapsed,
                        null,
                        null,
                        false,
                        0,
                        null,
                        $"Anthropic Messages 失败，状态码 {statusCode}。",
                        bodySample,
                        failureKind,
                        "Anthropic Messages",
                        $"POST {path} 返回 {statusCode} {response.ReasonPhrase}。{bodySample}",
                        headers,
                        RequestId: requestId,
                        TraceId: traceId),
                    null);
            }

            var preview = ParseAnthropicMessagesPreview(content) ?? BuildLooseSuccessPreview(content);
            if (!string.IsNullOrWhiteSpace(preview))
            {
                return new JsonProbeOutcome(
                    new ProxyProbeScenarioResult(
                        ProxyProbeScenarioKind.ChatCompletions,
                        "Anthropic Messages",
                        "支持",
                        true,
                        statusCode,
                        stopwatch.Elapsed,
                        null,
                        null,
                        false,
                        0,
                        null,
                        "Anthropic Messages 可用，已拿到可读返回内容。",
                        preview,
                        null,
                        "Anthropic Messages",
                        null,
                        headers,
                        RequestId: requestId,
                        TraceId: traceId),
                    preview);
            }

            return new JsonProbeOutcome(
                new ProxyProbeScenarioResult(
                    ProxyProbeScenarioKind.ChatCompletions,
                    "Anthropic Messages",
                    "异常",
                    false,
                    statusCode,
                    stopwatch.Elapsed,
                    null,
                    null,
                    false,
                    0,
                    null,
                    "Anthropic Messages 返回成功，但没有解析到可读内容，建议复核。",
                    ExtractBodySample(content),
                    ProxyFailureKind.ProtocolMismatch,
                    "Anthropic Messages",
                    "Anthropic Messages 返回成功，但没有解析到可读内容。",
                    headers),
                null);
        }
        catch (Exception ex) when (!IsCancellationRequestedException(ex, cancellationToken))
        {
            stopwatch.Stop();
            var failureKind = ClassifyException(ex);
            return new JsonProbeOutcome(
                new ProxyProbeScenarioResult(
                    ProxyProbeScenarioKind.ChatCompletions,
                    "Anthropic Messages",
                    DescribeCapability(failureKind, false),
                    false,
                    null,
                    stopwatch.Elapsed,
                    null,
                    null,
                    false,
                    0,
                    null,
                    $"Anthropic Messages 请求失败：{DescribeFailureKind(failureKind)}。",
                    null,
                    failureKind,
                    "Anthropic Messages",
                    $"POST {path} 请求失败：{ex.Message}",
                    RequestId: null,
                    TraceId: null),
                null);
        }
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
        _ = baseUrl;
        _ = model;
        _ = chatSupported;
        return responsesSupported ? "responses" : null;
    }

    private static string BuildProtocolProbeSummary(
        string model,
        bool chatSupported,
        bool responsesSupported,
        bool anthropicSupported,
        string? preferredWireApi)
    {
        var chatText = chatSupported ? "chat 可用" : "chat 不可用";
        var responsesText = responsesSupported ? "responses 可用" : "responses 不可用";
        var anthropicText = anthropicSupported ? "Anthropic messages 可用" : "Anthropic messages 不可用";
        var preferredText = string.IsNullOrWhiteSpace(preferredWireApi)
            ? "Codex 需要 responses，暂不可应用"
            : $"Codex 可写入 wire_api={preferredWireApi}";
        return $"协议探测模型：{model}；{chatText}，{responsesText}，{anthropicText}；{preferredText}。";
    }

    private static bool IsModelEligibleForChatCompletions(string? model)
        => !LooksLikeNonChatModel(model);

    private static bool IsModelEligibleForResponses(string? model)
    {
        var normalized = NormalizeProtocolModelName(model);
        if (string.IsNullOrWhiteSpace(normalized) ||
            IsModelEligibleForAnthropicMessages(normalized) ||
            LooksLikeNonChatModel(normalized))
        {
            return false;
        }

        string[] allowedMarkers =
        [
            "gpt-",
            "chatgpt-",
            "openai",
            "o1",
            "o3",
            "o4",
            "o5",
            "codex"
        ];

        return allowedMarkers.Any(marker => normalized.StartsWith(marker, StringComparison.Ordinal) ||
                                            normalized.Contains($"/{marker}", StringComparison.Ordinal));
    }

    private static bool IsModelEligibleForAnthropicMessages(string? model)
    {
        var normalized = NormalizeProtocolModelName(model);
        if (string.IsNullOrWhiteSpace(normalized) ||
            LooksLikeNonChatModel(normalized))
        {
            return false;
        }

        string[] anthropicMarkers =
        [
            "claude",
            "anthropic",
            "sonnet",
            "haiku",
            "opus"
        ];

        return anthropicMarkers.Any(marker => normalized.Contains(marker, StringComparison.Ordinal));
    }

    private static string NormalizeProtocolModelName(string? model)
    {
        var normalized = (model ?? string.Empty).Trim().ToLowerInvariant();
        return normalized
            .Replace("\\", "/", StringComparison.Ordinal)
            .Replace("_", "-", StringComparison.Ordinal)
            .Replace(" ", "-", StringComparison.Ordinal);
    }

    private static bool LooksLikeNonChatModel(string? model)
    {
        var normalized = NormalizeProtocolModelName(model);
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
