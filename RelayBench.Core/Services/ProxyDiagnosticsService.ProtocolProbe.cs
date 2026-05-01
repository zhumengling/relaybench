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
                "Protocol probe failed: invalid endpoint settings.",
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
                "Protocol probe failed: model is missing.",
                "Fill in a model name before probing protocol support.");
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
                    "Protocol probe failed: no model is available for probing.",
                    "Model name is empty.");
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
                    : "messages, responses and chat/completions probes all failed.");
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
                $"Protocol probe failed: {ex.Message}",
                ex.Message);
        }
    }

    private static async Task<ProxyWireApiDecision?> ProbeEndpointWireApiAsync(
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

        var anthropicPath = BuildApiPath(baseUri, "messages");
        var anthropicProbe = await ProbeAnthropicMessagesScenarioAsync(
            client,
            anthropicPath,
            BuildAnthropicMessagesPayload(probeModel),
            cancellationToken);
        var anthropicSupported = anthropicProbe.ScenarioResult.Success;
        List<ProxyProbeScenarioResult> scenarioResults = [anthropicProbe.ScenarioResult];

        var responsesPath = BuildApiPath(baseUri, "responses");
        var responsesProbe = await ProbeJsonScenarioAsync(
            client,
            responsesPath,
            BuildResponsesPayload(probeModel),
            ProxyProbeScenarioKind.Responses,
            "Responses",
            ParseResponsesPreview,
            cancellationToken);
        var responsesSupported = responsesProbe.ScenarioResult.Success;
        scenarioResults.Add(responsesProbe.ScenarioResult);

        var chatSupported = false;
        if (ShouldProbeChatCompletionsForProtocolProbe(anthropicSupported, responsesSupported))
        {
            var chatPath = BuildApiPath(baseUri, "chat/completions");
            var chatProbe = await ProbeJsonScenarioAsync(
                client,
                chatPath,
                BuildChatPayload(probeModel, stream: false),
                ProxyProbeScenarioKind.ChatCompletions,
                "OpenAI Chat Completions",
                ParseChatPreview,
                cancellationToken);
            chatSupported = chatProbe.ScenarioResult.Success;
            scenarioResults.Add(chatProbe.ScenarioResult);
        }

        var preferredWireApi = ResolvePreferredWireApi(
            chatSupported,
            responsesSupported,
            anthropicSupported);
        var summary = BuildProtocolProbeSummary(
            probeModel,
            chatSupported,
            responsesSupported,
            anthropicSupported,
            preferredWireApi);

        return new ProxyWireApiDecision(
            probeModel,
            chatSupported,
            responsesSupported,
            anthropicSupported,
            preferredWireApi,
            summary,
            scenarioResults);
    }

    private static async Task<JsonProbeOutcome> ProbeAnthropicMessagesScenarioAsync(
        HttpClient client,
        string path,
        string payload,
        CancellationToken cancellationToken,
        ProxyProbeScenarioKind scenario = ProxyProbeScenarioKind.AnthropicMessages,
        string displayName = "Anthropic Messages")
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json")
            };
            ConfigureAnthropicMessagesRequest(request, client);

            using var response = await client.SendAsync(request, cancellationToken);
            var statusCode = (int)response.StatusCode;
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            stopwatch.Stop();
            var headers = ExtractInterestingHeaders(response);
            var requestId = ExtractRequestId(headers);
            var traceId = ExtractTraceId(headers);

            JsonProbeOutcome BuildOutcome(ProxyProbeScenarioResult result, string? outcomePreview)
                => new(
                    result with
                    {
                        Trace = BuildProbeTrace(
                            client,
                            path,
                            payload,
                            result,
                            content,
                            headers,
                            outcomePreview)
                    },
                    outcomePreview);

            if (!response.IsSuccessStatusCode)
            {
                var failureKind = ClassifyResponseFailure(scenario, statusCode, content);
                var bodySample = ExtractBodySample(content);
                return BuildOutcome(
                    new ProxyProbeScenarioResult(
                        scenario,
                        displayName,
                        DescribeCapability(failureKind, false),
                        false,
                        statusCode,
                        stopwatch.Elapsed,
                        null,
                        null,
                        false,
                        0,
                        null,
                        $"{displayName} failed with HTTP {statusCode}.",
                        bodySample,
                        failureKind,
                        displayName,
                        $"POST {path} returned {statusCode} {response.ReasonPhrase}. {bodySample}",
                        headers,
                        RequestId: requestId,
                        TraceId: traceId),
                    null);
            }

            var preview = ParseAnthropicMessagesPreview(content) ?? BuildLooseSuccessPreview(content);
            if (!string.IsNullOrWhiteSpace(preview))
            {
                var outputMetrics = BuildOutputMetrics(preview, TryExtractOutputTokenCount(content), stopwatch.Elapsed);
                return BuildOutcome(
                    new ProxyProbeScenarioResult(
                        scenario,
                        displayName,
                        "Supported",
                        true,
                        statusCode,
                        stopwatch.Elapsed,
                        null,
                        null,
                        false,
                        0,
                        null,
                        $"{displayName} is available through Anthropic Messages.",
                        preview,
                        null,
                        displayName,
                        null,
                        headers,
                        OutputTokenCount: outputMetrics.OutputTokenCount,
                        OutputTokenCountEstimated: outputMetrics.OutputTokenCountEstimated,
                        OutputCharacterCount: outputMetrics.OutputCharacterCount,
                        GenerationDuration: outputMetrics.GenerationDuration,
                        OutputTokensPerSecond: outputMetrics.OutputTokensPerSecond,
                        EndToEndTokensPerSecond: outputMetrics.EndToEndTokensPerSecond,
                        RequestId: requestId,
                        TraceId: traceId),
                    preview);
            }

            return BuildOutcome(
                new ProxyProbeScenarioResult(
                    scenario,
                    displayName,
                    "Needs review",
                    false,
                    statusCode,
                    stopwatch.Elapsed,
                    null,
                    null,
                    false,
                    0,
                    null,
                    $"{displayName} returned success but no readable content was extracted.",
                    ExtractBodySample(content),
                    ProxyFailureKind.ProtocolMismatch,
                    displayName,
                    $"{displayName} returned success but no readable content was extracted.",
                    headers),
                null);
        }
        catch (Exception ex) when (!IsCancellationRequestedException(ex, cancellationToken))
        {
            stopwatch.Stop();
            var failureKind = ClassifyException(ex);
            return new JsonProbeOutcome(
                new ProxyProbeScenarioResult(
                    scenario,
                    displayName,
                    DescribeCapability(failureKind, false),
                    false,
                    null,
                    stopwatch.Elapsed,
                    null,
                    null,
                    false,
                    0,
                    null,
                    $"{displayName} request failed: {DescribeFailureKind(failureKind)}.",
                    null,
                    failureKind,
                    displayName,
                    $"POST {path} request failed: {ex.Message}",
                    RequestId: null,
                    TraceId: null),
                null);
        }
    }

    private static void ConfigureAnthropicMessagesRequest(HttpRequestMessage request, HttpClient client)
    {
        request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        var apiKey = client.DefaultRequestHeaders.Authorization?.Parameter;
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
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

    private static bool ShouldProbeChatCompletionsForProtocolProbe(
        bool anthropicSupported,
        bool responsesSupported)
        => ProxyWireApiProbeService.ShouldProbeChatCompletions(
            anthropicSupported,
            responsesSupported);

    private static string? ResolvePreferredWireApi(
        bool chatSupported,
        bool responsesSupported,
        bool anthropicSupported)
        => ProxyWireApiProbeService.ResolvePreferredWireApi(
            chatSupported,
            responsesSupported,
            anthropicSupported);

    private static string BuildProtocolProbeSummary(
        string model,
        bool chatSupported,
        bool responsesSupported,
        bool anthropicSupported,
        string? preferredWireApi)
        => ProxyWireApiProbeService.BuildSummary(
            model,
            chatSupported,
            responsesSupported,
            anthropicSupported,
            preferredWireApi);

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
