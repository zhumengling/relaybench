using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using RelayBench.Core.Models;
using RelayBench.Core.Support;

namespace RelayBench.Core.Services;

public sealed class ChatConversationService
{
    public async IAsyncEnumerable<ChatStreamUpdate> SendStreamingAsync(
        ChatRequestOptions options,
        IReadOnlyList<ChatMessage> history,
        IReadOnlyList<ChatAttachment> pendingAttachments,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!TryValidateOptions(options, out var normalizedOptions, out var baseUri, out var validationError))
        {
            yield return new ChatStreamUpdate(ChatStreamUpdateKind.Failed, null, null, validationError);
            yield break;
        }

        yield return new ChatStreamUpdate(ChatStreamUpdateKind.Started, null, null, null);

        var wireApis = BuildWireApiCandidates(normalizedOptions, history, pendingAttachments);
        ChatStreamUpdate? lastFailureUpdate = null;

        foreach (var wireApi in wireApis)
        {
            var stopwatch = Stopwatch.StartNew();
            TimeSpan? firstTokenLatency = null;
            StringBuilder outputBuilder = new();
            int? actualOutputTokenCount = null;
            int? actualInputTokenCount = null;
            int? actualCachedTokenCount = null;

            using var client = CreateClient(baseUri, normalizedOptions);
            var path = BuildApiPath(baseUri, ToEndpointName(wireApi));
            var payload = BuildPayloadForWireApi(wireApi, normalizedOptions, history);

            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            ConfigureRequestForWireApi(request, client, wireApi);

            HttpResponseMessage? response = null;
            Stream? stream = null;
            StreamReader? reader = null;
            ChatStreamUpdate? failureUpdate = null;
            string? nonStreamingBody = null;
            try
            {
                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(cancellationToken);
                    stopwatch.Stop();
                    failureUpdate = new ChatStreamUpdate(
                        ChatStreamUpdateKind.Failed,
                        null,
                        BuildMetrics(stopwatch.Elapsed, firstTokenLatency, outputBuilder.ToString(), wireApi),
                        BuildHttpFailureMessage((int)response.StatusCode, response.ReasonPhrase, body));
                }

                if (failureUpdate is null)
                {
                    var responseContentType = response.Content.Headers.ContentType?.ToString() ?? string.Empty;
                    if (IsEventStreamContentType(responseContentType))
                    {
                        stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                        reader = new StreamReader(stream, Encoding.UTF8);
                    }
                    else
                    {
                        nonStreamingBody = await response.Content.ReadAsStringAsync(cancellationToken);
                    }
                }
            }
            catch (Exception ex) when (!IsCancellationRequestedException(ex, cancellationToken))
            {
                stopwatch.Stop();
                failureUpdate = new ChatStreamUpdate(
                    ChatStreamUpdateKind.Failed,
                    null,
                    BuildMetrics(stopwatch.Elapsed, firstTokenLatency, outputBuilder.ToString(), wireApi),
                    BuildExceptionFailureMessage(ex));
            }

            if (failureUpdate is not null)
            {
                response?.Dispose();
                stream?.Dispose();
                reader?.Dispose();
                lastFailureUpdate = failureUpdate;
                continue;
            }

            if (nonStreamingBody is not null)
            {
                if (ChatSseParser.TryExtractOutputTokenCount(nonStreamingBody, out var nonStreamingOutputTokens))
                {
                    actualOutputTokenCount = nonStreamingOutputTokens;
                }
                if (ChatSseParser.TryExtractInputTokenCount(nonStreamingBody, out var nonStreamingInputTokens))
                {
                    actualInputTokenCount = nonStreamingInputTokens;
                }
                if (ChatSseParser.TryExtractCachedTokenCount(nonStreamingBody, out var nonStreamingCachedTokens))
                {
                    actualCachedTokenCount = nonStreamingCachedTokens;
                }

                var assistantText = ModelResponseTextExtractor.TryExtractAssistantText(nonStreamingBody);
                if (!string.IsNullOrEmpty(assistantText))
                {
                    foreach (var chunk in SplitFallbackStreamingChunks(assistantText))
                    {
                        outputBuilder.Append(chunk);
                        yield return new ChatStreamUpdate(ChatStreamUpdateKind.Delta, chunk, null, null);
                        await Task.Yield();
                    }

                    stopwatch.Stop();
                    yield return new ChatStreamUpdate(
                        ChatStreamUpdateKind.Completed,
                        null,
                        BuildMetrics(stopwatch.Elapsed, firstTokenLatency, outputBuilder.ToString(), wireApi, actualOutputTokenCount, actualInputTokenCount, actualCachedTokenCount),
                        null);
                    yield break;
                }

                // Try to surface an upstream error message embedded in the body.
                var upstreamError = TryExtractUpstreamError(nonStreamingBody);
                stopwatch.Stop();
                lastFailureUpdate = new ChatStreamUpdate(
                    ChatStreamUpdateKind.Failed,
                    null,
                    BuildMetrics(stopwatch.Elapsed, firstTokenLatency, outputBuilder.ToString(), wireApi, actualOutputTokenCount, actualInputTokenCount, actualCachedTokenCount),
                    BuildNonStreamingFailureMessage(wireApi, upstreamError, nonStreamingBody));
                response?.Dispose();
                continue;
            }

            var activeStream = stream!;
            var activeResponse = response!;
            var activeReader = reader!;
            var sawTerminalEvent = false;
            await using (activeStream)
            using (activeResponse)
            using (activeReader)
            {
                while (true)
                {
                    string? line;
                    try
                    {
                        line = await activeReader.ReadLineAsync(cancellationToken);
                    }
                    catch (Exception ex) when (!IsCancellationRequestedException(ex, cancellationToken))
                    {
                        stopwatch.Stop();
                        failureUpdate = new ChatStreamUpdate(
                            ChatStreamUpdateKind.Failed,
                            null,
                            BuildMetrics(stopwatch.Elapsed, firstTokenLatency, outputBuilder.ToString(), wireApi, actualOutputTokenCount, actualInputTokenCount, actualCachedTokenCount),
                            BuildExceptionFailureMessage(ex));
                        break;
                    }

                    if (failureUpdate is not null)
                    {
                        break;
                    }

                    if (line is null)
                    {
                        break;
                    }

                    if (!ChatSseParser.TryReadDataLine(line, out var data))
                    {
                        continue;
                    }

                    if (ChatSseParser.TryExtractOutputTokenCount(data, out var usageOutputTokens))
                    {
                        actualOutputTokenCount = usageOutputTokens;
                    }
                    if (ChatSseParser.TryExtractInputTokenCount(data, out var usageInputTokens))
                    {
                        actualInputTokenCount = usageInputTokens;
                    }
                    if (ChatSseParser.TryExtractCachedTokenCount(data, out var usageCachedTokens))
                    {
                        actualCachedTokenCount = usageCachedTokens;
                    }

                    if (ChatSseParser.IsDone(data))
                    {
                        sawTerminalEvent = true;
                        break;
                    }

                    string? delta;
                    try
                    {
                        delta = ChatSseParser.TryExtractDelta(data);
                        if (string.IsNullOrEmpty(delta) && outputBuilder.Length == 0)
                        {
                            delta = TryExtractInitialFullTextDelta(data);
                        }
                    }
                    catch
                    {
                        continue;
                    }

                    if (string.IsNullOrEmpty(delta))
                    {
                        continue;
                    }

                    firstTokenLatency ??= stopwatch.Elapsed;
                    outputBuilder.Append(delta);
                    yield return new ChatStreamUpdate(ChatStreamUpdateKind.Delta, delta, null, null);
                }
            }

            if (failureUpdate is not null)
            {
                if (outputBuilder.Length > 0)
                {
                    yield return failureUpdate;
                    yield break;
                }

                lastFailureUpdate = failureUpdate;
                continue;
            }

            if (!sawTerminalEvent)
            {
                stopwatch.Stop();
                failureUpdate = new ChatStreamUpdate(
                    ChatStreamUpdateKind.Failed,
                    null,
                    BuildMetrics(stopwatch.Elapsed, firstTokenLatency, outputBuilder.ToString(), wireApi, actualOutputTokenCount, actualInputTokenCount, actualCachedTokenCount),
                    "stream ended before a terminal event.");

                if (outputBuilder.Length > 0)
                {
                    yield return failureUpdate;
                    yield break;
                }

                lastFailureUpdate = failureUpdate;
                continue;
            }

            if (outputBuilder.Length == 0)
            {
                stopwatch.Stop();
                lastFailureUpdate = new ChatStreamUpdate(
                    ChatStreamUpdateKind.Failed,
                    null,
                    BuildMetrics(stopwatch.Elapsed, firstTokenLatency, outputBuilder.ToString(), wireApi, actualOutputTokenCount, actualInputTokenCount, actualCachedTokenCount),
                    $"{wireApi} returned an empty streaming response.");
                continue;
            }

            stopwatch.Stop();
            yield return new ChatStreamUpdate(
                ChatStreamUpdateKind.Completed,
                null,
                BuildMetrics(stopwatch.Elapsed, firstTokenLatency, outputBuilder.ToString(), wireApi, actualOutputTokenCount, actualInputTokenCount, actualCachedTokenCount),
                null);
            yield break;
        }

        if (lastFailureUpdate is not null)
        {
            yield return lastFailureUpdate;
        }
    }

    private static IReadOnlyList<string> BuildWireApiCandidates(
        ChatRequestOptions options,
        IReadOnlyList<ChatMessage> history,
        IReadOnlyList<ChatAttachment> pendingAttachments)
    {
        List<string> candidates = [];
        var preferredWireApi = ProxyWireApiProbeService.NormalizeWireApi(options.PreferredWireApi);
        if (string.Equals(preferredWireApi, ProxyWireApiProbeService.ResponsesWireApi, StringComparison.Ordinal) ||
            string.Equals(preferredWireApi, ProxyWireApiProbeService.AnthropicMessagesWireApi, StringComparison.Ordinal))
        {
            AddCandidate(candidates, preferredWireApi);
        }

        if (!HasImageAttachments(history, pendingAttachments))
        {
            AddCandidate(candidates, ProxyWireApiProbeService.ResponsesWireApi);
            AddCandidate(candidates, ProxyWireApiProbeService.AnthropicMessagesWireApi);
            AddCandidate(candidates, ProxyWireApiProbeService.ChatCompletionsWireApi);
        }
        else
        {
            AddCandidate(candidates, ProxyWireApiProbeService.ResponsesWireApi);
            AddCandidate(candidates, ProxyWireApiProbeService.AnthropicMessagesWireApi);
            AddCandidate(candidates, ProxyWireApiProbeService.ChatCompletionsWireApi);
        }

        return candidates;
    }

    private static void AddCandidate(List<string> candidates, string? wireApi)
    {
        if (string.IsNullOrWhiteSpace(wireApi) ||
            candidates.Contains(wireApi, StringComparer.Ordinal))
        {
            return;
        }

        candidates.Add(wireApi);
    }

    private static string ToEndpointName(string wireApi)
        => wireApi switch
        {
            ProxyWireApiProbeService.ResponsesWireApi => "responses",
            ProxyWireApiProbeService.AnthropicMessagesWireApi => "messages",
            _ => "chat/completions"
        };

    private static string BuildPayloadForWireApi(
        string wireApi,
        ChatRequestOptions options,
        IReadOnlyList<ChatMessage> history)
        => wireApi switch
        {
            ProxyWireApiProbeService.ResponsesWireApi => ChatRequestPayloadBuilder.BuildResponsesPayload(options, history),
            ProxyWireApiProbeService.AnthropicMessagesWireApi => ChatRequestPayloadBuilder.BuildAnthropicMessagesPayload(options, history),
            _ => ChatRequestPayloadBuilder.BuildChatCompletionsPayload(options, history)
        };

    private static void ConfigureRequestForWireApi(
        HttpRequestMessage request,
        HttpClient client,
        string wireApi)
    {
        if (!string.Equals(wireApi, ProxyWireApiProbeService.AnthropicMessagesWireApi, StringComparison.Ordinal))
        {
            return;
        }

        request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        var apiKey = client.DefaultRequestHeaders.Authorization?.Parameter;
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        }
    }

    private static bool HasImageAttachments(
        IReadOnlyList<ChatMessage> history,
        IReadOnlyList<ChatAttachment> pendingAttachments)
        => pendingAttachments.Any(static attachment => attachment.Kind == ChatAttachmentKind.Image) ||
           history.Any(static message => message.Attachments.Any(static attachment => attachment.Kind == ChatAttachmentKind.Image));

    private static bool TryValidateOptions(
        ChatRequestOptions options,
        out ChatRequestOptions normalizedOptions,
        out Uri baseUri,
        out string error)
    {
        normalizedOptions = options;
        baseUri = null!;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            error = "\u8bf7\u5148\u586b\u5199\u63a5\u53e3\u5730\u5740\u3002";
            return false;
        }

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            error = "\u8bf7\u5148\u586b\u5199 API Key\u3002";
            return false;
        }

        if (string.IsNullOrWhiteSpace(options.Model))
        {
            error = "\u8bf7\u5148\u9009\u62e9\u6216\u586b\u5199\u6a21\u578b\u3002";
            return false;
        }

        if (!Uri.TryCreate(options.BaseUrl.Trim(), UriKind.Absolute, out var parsedUri))
        {
            error = "\u63a5\u53e3\u5730\u5740\u4e0d\u662f\u6709\u6548\u7684\u7edd\u5bf9 URI\u3002";
            return false;
        }

        baseUri = EnsureTrailingSlash(parsedUri);
        normalizedOptions = options with
        {
            BaseUrl = baseUri.ToString(),
            ApiKey = options.ApiKey.Trim(),
            Model = options.Model.Trim(),
            Temperature = Math.Clamp(options.Temperature, 0d, 2d),
            MaxTokens = Math.Clamp(options.MaxTokens, 1, 200_000),
            TimeoutSeconds = Math.Clamp(options.TimeoutSeconds, 5, 300)
        };
        return true;
    }

    private static HttpClient CreateClient(Uri baseUri, ChatRequestOptions options)
    {
        HttpClientHandler handler = new();
        if (options.IgnoreTlsErrors)
        {
            handler.ServerCertificateCustomValidationCallback = static (_, _, _, _) => true;
        }

        var client = new HttpClient(handler)
        {
            BaseAddress = baseUri,
            Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds)
        };

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RelayBenchChat/0.1");
        return client;
    }

    private static Uri EnsureTrailingSlash(Uri baseUri)
        => baseUri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? baseUri
            : new Uri($"{baseUri.AbsoluteUri}/", UriKind.Absolute);

    private static string BuildApiPath(Uri baseUri, string endpoint)
        => EndpointPathBuilder.BuildOpenAiCompatiblePath(baseUri, endpoint);

    private static bool IsEventStreamContentType(string? contentType)
        => contentType?.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase) == true;

    private static IEnumerable<string> SplitFallbackStreamingChunks(string text)
    {
        const int targetChunkLength = 96;
        for (var index = 0; index < text.Length;)
        {
            var remaining = text.Length - index;
            var length = Math.Min(targetChunkLength, remaining);
            if (remaining > targetChunkLength)
            {
                for (var offset = length - 1; offset >= Math.Max(24, length / 2); offset--)
                {
                    if (char.IsWhiteSpace(text[index + offset]) ||
                        "，。！？；,.!?;".IndexOf(text[index + offset]) >= 0)
                    {
                        length = offset + 1;
                        break;
                    }
                }
            }

            yield return text.Substring(index, length);
            index += length;
        }
    }

    private static string? TryExtractInitialFullTextDelta(string data)
    {
        if (string.IsNullOrWhiteSpace(data) ||
            string.Equals(data.Trim(), "[DONE]", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;
            if (root.TryGetProperty("type", out var typeElement) &&
                typeElement.ValueKind == JsonValueKind.String)
            {
                var type = typeElement.GetString();
                if ((string.Equals(type, "response.output_text.done", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(type, "response.content_part.added", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(type, "response.output_item.done", StringComparison.OrdinalIgnoreCase)) &&
                    ModelResponseTextExtractor.TryExtractAssistantText(root) is { Length: > 0 } text)
                {
                    return text;
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static ChatMessageMetrics BuildMetrics(
        TimeSpan elapsed,
        TimeSpan? firstTokenLatency,
        string outputText,
        string wireApi,
        int? actualOutputTokenCount = null,
        int? actualInputTokenCount = null,
        int? actualCachedTokenCount = null)
    {
        var outputCharacters = outputText.Length;
        double? charactersPerSecond = elapsed.TotalSeconds > 0
            ? outputCharacters / elapsed.TotalSeconds
            : null;
        var estimatedOutputTokenCount = TokenCountEstimator.EstimateOutputTokens(outputText);
        var hasActualOutputTokens = actualOutputTokenCount is > 0;
        var outputTokenCount = hasActualOutputTokens
            ? actualOutputTokenCount!.Value
            : estimatedOutputTokenCount;
        var generationDuration = firstTokenLatency is { } ttft && elapsed > ttft
            ? elapsed - ttft
            : elapsed;
        var throughputWindow = ResolveThroughputWindow(elapsed, generationDuration, outputTokenCount);
        var tokensPerSecond = outputTokenCount > 0 && throughputWindow > TimeSpan.Zero
            ? outputTokenCount / throughputWindow.TotalSeconds
            : (double?)null;

        return new ChatMessageMetrics(elapsed, firstTokenLatency, outputCharacters, charactersPerSecond, wireApi)
        {
            OutputTokenCount = outputTokenCount,
            OutputTokenCountEstimated = outputTokenCount > 0 && !hasActualOutputTokens,
            InputTokenCount = Math.Max(0, actualInputTokenCount ?? 0),
            CachedTokenCount = Math.Max(0, actualCachedTokenCount ?? 0),
            TokenThroughputWindow = throughputWindow > TimeSpan.Zero ? throughputWindow : null,
            TokensPerSecond = tokensPerSecond
        };
    }

    private static TimeSpan ResolveThroughputWindow(
        TimeSpan elapsed,
        TimeSpan generationDuration,
        int outputTokenCount)
    {
        if (generationDuration <= TimeSpan.Zero || elapsed <= TimeSpan.Zero || elapsed <= generationDuration)
        {
            return generationDuration;
        }

        if (generationDuration < TimeSpan.FromMilliseconds(20))
        {
            return elapsed;
        }

        if (outputTokenCount is > 0 and < 8 &&
            generationDuration < TimeSpan.FromMilliseconds(120))
        {
            return elapsed;
        }

        return generationDuration;
    }

    private static string BuildNonStreamingFailureMessage(string wireApi, string? upstreamError, string body)
    {
        var apiLabel = wireApi switch
        {
            ProxyWireApiProbeService.ResponsesWireApi => "Responses",
            ProxyWireApiProbeService.AnthropicMessagesWireApi => "Anthropic Messages",
            _ => "OpenAI Chat Completions"
        };

        var sb = new StringBuilder();
        sb.Append("上游 ").Append(apiLabel).Append(" 接口返回了非流式内容，但未能提取模型回复");
        if (!string.IsNullOrWhiteSpace(upstreamError))
        {
            sb.Append("。错误：").Append(upstreamError);
            return sb.ToString();
        }

        // Surface the first meaningful slice of the body so users can see what the upstream said.
        var preview = BuildBodyPreview(body);
        if (!string.IsNullOrWhiteSpace(preview))
        {
            sb.Append("。响应体预览：").Append(preview);
        }
        else
        {
            sb.Append('。');
        }
        return sb.ToString();
    }

    private static string BuildBodyPreview(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return string.Empty;
        var trimmed = body.Trim();
        if (trimmed.Length > 240)
        {
            trimmed = trimmed[..240] + "…";
        }
        return trimmed;
    }

    private static string? TryExtractUpstreamError(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object)
            {
                // OpenAI / RelayBench shape: { error: { message: "..." } }
                if (root.TryGetProperty("error", out var err))
                {
                    if (err.ValueKind == JsonValueKind.String)
                    {
                        return err.GetString();
                    }
                    if (err.ValueKind == JsonValueKind.Object)
                    {
                        if (err.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                            return m.GetString();
                        if (err.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String)
                            return c.GetString();
                    }
                }
                // Anthropic shape: { type: "error", error: { type:..., message:... } }
                if (root.TryGetProperty("type", out var t)
                    && t.ValueKind == JsonValueKind.String
                    && string.Equals(t.GetString(), "error", StringComparison.OrdinalIgnoreCase)
                    && root.TryGetProperty("error", out var anthErr)
                    && anthErr.ValueKind == JsonValueKind.Object
                    && anthErr.TryGetProperty("message", out var anthMsg)
                    && anthMsg.ValueKind == JsonValueKind.String)
                {
                    return anthMsg.GetString();
                }
                // Generic message / detail fields
                if (root.TryGetProperty("message", out var genericMsg) && genericMsg.ValueKind == JsonValueKind.String)
                    return genericMsg.GetString();
                if (root.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String)
                    return detail.GetString();
            }
        }
        catch (JsonException) { }
        return null;
    }

    private static string BuildHttpFailureMessage(int statusCode, string? reasonPhrase, string body)
    {
        var detail = ChatSseParser.TryExtractError(body);
        if (TryExtractRelayBenchError(body, out var relayBenchCode, out var relayBenchMessage))
        {
            var friendlyReason = relayBenchCode switch
            {
                "codex_oauth_login_required" => "Codex OAuth 账号需要重新登录或重新导入令牌",
                "codex_oauth_disabled" => "Codex OAuth 账号已停用",
                "transparent_proxy_not_ready" => "本地统一出口还没有可用上游路由",
                "relaybench_upstream_unavailable" => "本地统一出口的上游路由暂不可用",
                _ => "本地统一出口请求失败"
            };
            var relayBenchDetail = string.IsNullOrWhiteSpace(relayBenchMessage)
                ? detail
                : relayBenchMessage;
            return $"{friendlyReason}。HTTP {statusCode} {reasonPhrase}".Trim() +
                (string.IsNullOrWhiteSpace(relayBenchDetail) ? string.Empty : $"\n{relayBenchDetail}");
        }

        var reason = statusCode switch
        {
            400 when ContainsAny(body, "image", "vision", "multimodal") => "\u56fe\u7247\u8f93\u5165\u6216\u591a\u6a21\u6001\u683c\u5f0f\u4e0d\u517c\u5bb9",
            400 when ContainsAny(body, "reasoning", "unsupported", "unknown parameter") => "\u5f53\u524d\u63a5\u53e3\u4e0d\u652f\u6301 reasoning \u53c2\u6570",
            400 => "\u8bf7\u6c42\u53c2\u6570\u6216\u6a21\u578b\u683c\u5f0f\u4e0d\u517c\u5bb9",
            401 or 403 => "\u8ba4\u8bc1\u5931\u8d25\u6216\u6743\u9650\u4e0d\u8db3",
            404 => "\u63a5\u53e3\u8def\u5f84\u6216\u6a21\u578b\u4e0d\u5b58\u5728",
            408 => "\u8bf7\u6c42\u8d85\u65f6",
            429 => "\u63a5\u53e3\u9650\u6d41\u6216\u989d\u5ea6\u4e0d\u8db3",
            >= 500 => "\u4e0a\u6e38\u670d\u52a1\u5668\u5f02\u5e38",
            _ => "\u8bf7\u6c42\u5931\u8d25"
        };

        return $"{reason}\u3002HTTP {statusCode} {reasonPhrase}".Trim() +
            (string.IsNullOrWhiteSpace(detail) ? string.Empty : $"\n{detail}");
    }

    private static bool TryExtractRelayBenchError(string body, out string code, out string message)
    {
        code = string.Empty;
        message = string.Empty;
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("error", out var error) ||
                error.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            code = TryReadErrorString(error, "code") ?? string.Empty;
            message = TryReadErrorString(error, "message") ?? string.Empty;
            return string.Equals(TryReadErrorString(error, "type"), "relaybench_transparent_proxy_error", StringComparison.OrdinalIgnoreCase) ||
                   code.StartsWith("codex_oauth_", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(code, "transparent_proxy_not_ready", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(code, "relaybench_upstream_unavailable", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string? TryReadErrorString(JsonElement error, string propertyName)
        => error.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string BuildExceptionFailureMessage(Exception exception)
    {
        if (exception is TaskCanceledException)
        {
            return "\u8bf7\u6c42\u8d85\u65f6\u6216\u5df2\u53d6\u6d88\u3002";
        }

        if (exception is HttpRequestException { InnerException: SocketException socketException })
        {
            return socketException.SocketErrorCode switch
            {
                SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain => "\u57df\u540d\u89e3\u6790\u5931\u8d25\u3002",
                SocketError.ConnectionRefused => "\u76ee\u6807\u62d2\u7edd\u8fde\u63a5\u3002",
                SocketError.ConnectionReset => "\u8fde\u63a5\u88ab\u4e0a\u6e38\u91cd\u7f6e\u3002",
                SocketError.TimedOut => "\u8fde\u63a5\u8d85\u65f6\u3002",
                _ => $"\u7f51\u7edc\u8fde\u63a5\u5931\u8d25\uFF1A{socketException.SocketErrorCode}\u3002"
            };
        }

        if (exception is HttpRequestException)
        {
            return $"\u8bf7\u6c42\u53d1\u9001\u5931\u8d25\uFF1A{exception.Message}";
        }

        return exception.Message;
    }

    private static bool ContainsAny(string text, params string[] values)
        => values.Any(value => text.Contains(value, StringComparison.OrdinalIgnoreCase));

    private static bool IsCancellationRequestedException(Exception ex, CancellationToken cancellationToken)
        => cancellationToken.IsCancellationRequested &&
           (ex is OperationCanceledException or TaskCanceledException);
}
