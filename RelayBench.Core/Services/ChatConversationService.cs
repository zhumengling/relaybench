using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using RelayBench.Core.Models;

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
            var outputCharacters = 0;

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
                        BuildMetrics(stopwatch.Elapsed, firstTokenLatency, outputCharacters, wireApi),
                        BuildHttpFailureMessage((int)response.StatusCode, response.ReasonPhrase, body));
                }

                if (failureUpdate is null)
                {
                    stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    reader = new StreamReader(stream, Encoding.UTF8);
                }
            }
            catch (Exception ex) when (!IsCancellationRequestedException(ex, cancellationToken))
            {
                stopwatch.Stop();
                failureUpdate = new ChatStreamUpdate(
                    ChatStreamUpdateKind.Failed,
                    null,
                    BuildMetrics(stopwatch.Elapsed, firstTokenLatency, outputCharacters, wireApi),
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
                            BuildMetrics(stopwatch.Elapsed, firstTokenLatency, outputCharacters, wireApi),
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

                    if (ChatSseParser.IsDone(data))
                    {
                        sawTerminalEvent = true;
                        break;
                    }

                    string? delta;
                    try
                    {
                        delta = ChatSseParser.TryExtractDelta(data);
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
                    outputCharacters += delta.Length;
                    yield return new ChatStreamUpdate(ChatStreamUpdateKind.Delta, delta, null, null);
                }
            }

            if (failureUpdate is not null)
            {
                if (outputCharacters > 0)
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
                    BuildMetrics(stopwatch.Elapsed, firstTokenLatency, outputCharacters, wireApi),
                    "stream ended before a terminal event.");

                if (outputCharacters > 0)
                {
                    yield return failureUpdate;
                    yield break;
                }

                lastFailureUpdate = failureUpdate;
                continue;
            }

            stopwatch.Stop();
            yield return new ChatStreamUpdate(
                ChatStreamUpdateKind.Completed,
                null,
                BuildMetrics(stopwatch.Elapsed, firstTokenLatency, outputCharacters, wireApi),
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
        AddCandidate(candidates, ProxyWireApiProbeService.NormalizeWireApi(options.PreferredWireApi));

        if (ShouldUseResponsesApi(options, history, pendingAttachments))
        {
            AddCandidate(candidates, ProxyWireApiProbeService.ResponsesWireApi);
            AddCandidate(candidates, ProxyWireApiProbeService.AnthropicMessagesWireApi);
            AddCandidate(candidates, ProxyWireApiProbeService.ChatCompletionsWireApi);
        }
        else
        {
            AddCandidate(candidates, ProxyWireApiProbeService.ChatCompletionsWireApi);
            AddCandidate(candidates, ProxyWireApiProbeService.AnthropicMessagesWireApi);
            AddCandidate(candidates, ProxyWireApiProbeService.ResponsesWireApi);
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

    private static bool ShouldUseResponsesApi(
        ChatRequestOptions options,
        IReadOnlyList<ChatMessage> history,
        IReadOnlyList<ChatAttachment> pendingAttachments)
    {
        if (!options.PreferResponsesApi || options.ReasoningEffort is ChatReasoningEffort.Auto)
        {
            return false;
        }

        var hasImages = pendingAttachments.Any(static attachment => attachment.Kind == ChatAttachmentKind.Image) ||
            history.Any(static message => message.Attachments.Any(static attachment => attachment.Kind == ChatAttachmentKind.Image));
        return !hasImages;
    }

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

    private static ChatMessageMetrics BuildMetrics(
        TimeSpan elapsed,
        TimeSpan? firstTokenLatency,
        int outputCharacters,
        string wireApi)
    {
        double? charactersPerSecond = elapsed.TotalSeconds > 0
            ? outputCharacters / elapsed.TotalSeconds
            : null;
        return new ChatMessageMetrics(elapsed, firstTokenLatency, outputCharacters, charactersPerSecond, wireApi);
    }

    private static string BuildHttpFailureMessage(int statusCode, string? reasonPhrase, string body)
    {
        var detail = ChatSseParser.TryExtractError(body);
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
