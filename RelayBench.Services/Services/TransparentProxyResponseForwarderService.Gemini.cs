using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RelayBench.Core.Services;
using RelayBench.Core.Support;

namespace RelayBench.Services;

internal sealed partial class TransparentProxyResponseForwarderService
{
    private async Task CopyNormalizedGeminiJsonAsync(
        HttpListenerContext context,
        HttpResponseMessage upstreamResponse,
        int statusCode,
        string cacheKey,
        TransparentProxyServerConfig config,
        string responseModel,
        string logModel,
        string wireApi,
        IReadOnlyDictionary<string, string>? toolNameAliases,
        CancellationToken cancellationToken)
    {
        var upstreamBytes = await upstreamResponse.Content.ReadAsByteArrayAsync(cancellationToken);
        var upstreamText = Encoding.UTF8.GetString(upstreamBytes);
        TrackPromptCacheTokens(upstreamText);
        var normalized = _responseNormalizer.TryBuildNormalizedChatJson(
            upstreamBytes,
            responseModel,
            wireApi,
            toolNameAliases);
        if (normalized is null)
        {
            context.Response.ContentType = upstreamResponse.Content.Headers.ContentType?.ToString() ?? "application/json";
            context.Response.ContentLength64 = upstreamBytes.LongLength;
            await context.Response.OutputStream.WriteAsync(upstreamBytes, cancellationToken);
            TrackResponseBodyTokens(upstreamBytes);
            context.Response.OutputStream.Close();
            return;
        }

        var normalizedBytes = BuildGeminiGenerateContentBytes(
            normalized.AssistantText,
            normalized.ReasoningText,
            normalized.ToolCalls,
            normalized.Images,
            normalized.Usage,
            ResolveGeminiResponseModel(responseModel, string.Empty),
            "STOP");
        await WriteGeminiJsonResponseAsync(
            context,
            normalizedBytes,
            statusCode,
            cacheKey,
            config,
            logModel,
            cancellationToken);
    }

    private async Task CopyNormalizedGeminiJsonAsStreamAsync(
        HttpListenerContext context,
        HttpResponseMessage upstreamResponse,
        string responseModel,
        string wireApi,
        IReadOnlyDictionary<string, string>? toolNameAliases,
        CancellationToken cancellationToken)
    {
        var upstreamBytes = await upstreamResponse.Content.ReadAsByteArrayAsync(cancellationToken);
        var upstreamText = Encoding.UTF8.GetString(upstreamBytes);
        TrackPromptCacheTokens(upstreamText);
        var normalized = _responseNormalizer.TryBuildNormalizedChatJson(
            upstreamBytes,
            responseModel,
            wireApi,
            toolNameAliases);
        if (normalized is null)
        {
            context.Response.ContentType = upstreamResponse.Content.Headers.ContentType?.ToString() ?? "application/json";
            context.Response.ContentLength64 = upstreamBytes.LongLength;
            await context.Response.OutputStream.WriteAsync(upstreamBytes, cancellationToken);
            TrackResponseBodyTokens(upstreamBytes);
            context.Response.OutputStream.Close();
            return;
        }

        context.Response.ContentType = "text/event-stream; charset=utf-8";
        context.Response.SendChunked = true;
        var data = BuildGeminiGenerateContentJson(
            normalized.AssistantText,
            normalized.ReasoningText,
            normalized.ToolCalls,
            normalized.Images,
            normalized.Usage,
            ResolveGeminiResponseModel(responseModel, string.Empty),
            "STOP");
        await WriteSseDataAsync(context.Response.OutputStream, data, cancellationToken);
        context.Response.OutputStream.Close();
    }

    private async Task CopyNormalizedGeminiEventStreamAsJsonAsync(
        HttpListenerContext context,
        HttpResponseMessage upstreamResponse,
        int statusCode,
        string cacheKey,
        TransparentProxyServerConfig config,
        string responseModel,
        string logModel,
        string wireApi,
        IReadOnlyDictionary<string, string>? toolNameAliases,
        CancellationToken cancellationToken)
    {
        List<TransparentProxySseEvent> events = [];
        await foreach (var sseEvent in _sseFramer.ReadEventsAsync(upstreamResponse.Content, cancellationToken))
        {
            events.Add(sseEvent);
            TrackPromptCacheTokens(sseEvent.Data);
        }

        var aggregate = BuildChatAggregateFromSseEvents(
            events,
            responseModel,
            wireApi,
            toolNameAliases,
            _responseNormalizer);
        var normalizedBytes = BuildGeminiGenerateContentBytes(
            aggregate.AssistantText,
            aggregate.ReasoningText,
            aggregate.ToolCalls,
            aggregate.Images,
            aggregate.Usage,
            ResolveGeminiResponseModel(responseModel, aggregate.Model),
            "STOP");
        await WriteGeminiJsonResponseAsync(
            context,
            normalizedBytes,
            statusCode,
            cacheKey,
            config,
            logModel,
            cancellationToken);
    }

    private async Task CopyNormalizedGeminiStreamAsync(
        HttpListenerContext context,
        HttpResponseMessage upstreamResponse,
        string responseModel,
        string wireApi,
        IReadOnlyDictionary<string, string>? toolNameAliases,
        CancellationToken cancellationToken)
    {
        List<TransparentProxySseEvent> events = [];
        await foreach (var sseEvent in _sseFramer.ReadEventsAsync(upstreamResponse.Content, cancellationToken))
        {
            events.Add(sseEvent);
            TrackPromptCacheTokens(sseEvent.Data);
        }

        var aggregate = BuildChatAggregateFromSseEvents(
            events,
            responseModel,
            wireApi,
            toolNameAliases,
            _responseNormalizer);

        context.Response.ContentType = "text/event-stream; charset=utf-8";
        context.Response.SendChunked = true;
        var data = BuildGeminiGenerateContentJson(
            aggregate.AssistantText,
            aggregate.ReasoningText,
            aggregate.ToolCalls,
            aggregate.Images,
            aggregate.Usage,
            ResolveGeminiResponseModel(responseModel, aggregate.Model),
            "STOP");
        await WriteSseDataAsync(context.Response.OutputStream, data, cancellationToken);
        context.Response.OutputStream.Close();
    }

    private async Task WriteGeminiJsonResponseAsync(
        HttpListenerContext context,
        byte[] normalizedBytes,
        int statusCode,
        string cacheKey,
        TransparentProxyServerConfig config,
        string logModel,
        CancellationToken cancellationToken)
    {
        const string normalizedContentType = "application/json; charset=utf-8";
        context.Response.ContentType = normalizedContentType;
        context.Response.ContentLength64 = normalizedBytes.LongLength;
        await context.Response.OutputStream.WriteAsync(normalizedBytes, cancellationToken);
        TrackResponseBodyTokens(normalizedBytes, includePromptCache: false);

        if (config.EnableCache &&
            !string.IsNullOrWhiteSpace(cacheKey) &&
            statusCode >= 200 &&
            statusCode < 300 &&
            normalizedBytes.Length <= config.CacheMaxBytes)
        {
            _responseCache.StoreResponse(
                cacheKey,
                statusCode,
                normalizedContentType,
                normalizedBytes,
                NormalizeLogModel(logModel),
                config.CacheMaxBytes);
        }

        context.Response.OutputStream.Close();
    }

    internal static byte[] BuildGeminiGenerateContentBytes(
        string assistantText,
        string reasoningText,
        IReadOnlyList<TransparentProxyResponseNormalizationService.TransparentProxyNormalizedToolCall> toolCalls,
        IReadOnlyList<TransparentProxyResponseNormalizationService.TransparentProxyNormalizedImage> images,
        JsonNode? usage,
        string model,
        string finishReason)
        => JsonSerializer.SerializeToUtf8Bytes(BuildGeminiGenerateContentNode(
            assistantText,
            reasoningText,
            toolCalls,
            images,
            usage,
            model,
            finishReason));

    private static string BuildGeminiGenerateContentJson(
        string assistantText,
        string reasoningText,
        IReadOnlyList<TransparentProxyResponseNormalizationService.TransparentProxyNormalizedToolCall> toolCalls,
        IReadOnlyList<TransparentProxyResponseNormalizationService.TransparentProxyNormalizedImage> images,
        JsonNode? usage,
        string model,
        string finishReason)
        => BuildGeminiGenerateContentNode(
            assistantText,
            reasoningText,
            toolCalls,
            images,
            usage,
            model,
            finishReason).ToJsonString();

    private static JsonObject BuildGeminiGenerateContentNode(
        string assistantText,
        string reasoningText,
        IReadOnlyList<TransparentProxyResponseNormalizationService.TransparentProxyNormalizedToolCall> toolCalls,
        IReadOnlyList<TransparentProxyResponseNormalizationService.TransparentProxyNormalizedImage> images,
        JsonNode? usage,
        string model,
        string finishReason)
    {
        JsonArray parts = [];
        if (!string.IsNullOrWhiteSpace(reasoningText))
        {
            parts.Add(new JsonObject
            {
                ["thought"] = true,
                ["text"] = reasoningText
            });
        }

        if (!string.IsNullOrEmpty(assistantText))
        {
            parts.Add(new JsonObject
            {
                ["text"] = assistantText
            });
        }

        foreach (var image in images)
        {
            parts.Add(new JsonObject
            {
                ["inlineData"] = new JsonObject
                {
                    ["mimeType"] = string.IsNullOrWhiteSpace(image.MimeType) ? "image/png" : image.MimeType,
                    ["data"] = image.Base64Data
                }
            });
        }

        foreach (var toolCall in toolCalls)
        {
            parts.Add(new JsonObject
            {
                ["functionCall"] = new JsonObject
                {
                    ["name"] = toolCall.Name,
                    ["args"] = ParseGeminiToolArguments(toolCall.Arguments)
                }
            });
        }

        var usageMetadata = BuildGeminiUsageMetadata(usage, assistantText);
        return new JsonObject
        {
            ["candidates"] = new JsonArray
            {
                new JsonObject
                {
                    ["content"] = new JsonObject
                    {
                        ["role"] = "model",
                        ["parts"] = parts
                    },
                    ["finishReason"] = string.IsNullOrWhiteSpace(finishReason) ? "STOP" : finishReason
                }
            },
            ["usageMetadata"] = usageMetadata,
            ["modelVersion"] = ResolveGeminiResponseModel(model, string.Empty),
            ["createTime"] = DateTimeOffset.UtcNow.ToString("O"),
            ["responseId"] = $"relaybench-{Guid.NewGuid():N}"
        };
    }

    private static JsonObject BuildGeminiUsageMetadata(JsonNode? usage, string assistantText)
    {
        var promptTokens = ReadUsageToken(usage, "promptTokenCount", "prompt_tokens", "input_tokens");
        var directCandidateTokens = ReadUsageToken(usage, "candidatesTokenCount");
        var reasoningTokens = ReadUsageToken(usage, "thoughtsTokenCount", "reasoning_tokens");
        var candidateTokens = directCandidateTokens > 0
            ? directCandidateTokens
            : ReadUsageToken(usage, "completion_tokens", "output_tokens");
        if (directCandidateTokens == 0 &&
            reasoningTokens > 0 &&
            candidateTokens > reasoningTokens)
        {
            candidateTokens -= reasoningTokens;
        }

        if (candidateTokens == 0)
        {
            candidateTokens = Math.Max(0, TokenCountEstimator.EstimateOutputTokens(assistantText));
        }

        var totalTokens = ReadUsageToken(usage, "totalTokenCount", "total_tokens");
        if (totalTokens == 0)
        {
            totalTokens = promptTokens + candidateTokens + reasoningTokens;
        }

        JsonObject metadata = new()
        {
            ["trafficType"] = "PROVISIONED_THROUGHPUT",
            ["promptTokenCount"] = promptTokens,
            ["candidatesTokenCount"] = candidateTokens,
            ["totalTokenCount"] = totalTokens
        };

        var cachedTokens = ReadUsageToken(
            usage,
            "cachedContentTokenCount",
            "cached_tokens",
            "cache_read_input_tokens");
        if (cachedTokens > 0)
        {
            metadata["cachedContentTokenCount"] = cachedTokens;
        }

        if (reasoningTokens > 0)
        {
            metadata["thoughtsTokenCount"] = reasoningTokens;
        }

        return metadata;
    }

    private static int ReadUsageToken(JsonNode? usage, params string[] names)
    {
        if (usage is not JsonObject obj)
        {
            return 0;
        }

        foreach (var name in names)
        {
            if (obj.TryGetPropertyValue(name, out var node) &&
                TryReadInt(node, out var value))
            {
                return Math.Max(0, value);
            }
        }

        foreach (var child in obj.Select(static property => property.Value))
        {
            var nested = ReadUsageToken(child, names);
            if (nested > 0)
            {
                return nested;
            }
        }

        return 0;
    }

    private static bool TryReadInt(JsonNode? node, out int value)
    {
        value = 0;
        if (node is not JsonValue jsonValue)
        {
            return false;
        }

        if (jsonValue.TryGetValue<int>(out value))
        {
            return true;
        }

        if (jsonValue.TryGetValue<long>(out var longValue))
        {
            value = (int)Math.Min(int.MaxValue, Math.Max(0, longValue));
            return true;
        }

        if (jsonValue.TryGetValue<string>(out var text) &&
            int.TryParse(text, out value))
        {
            return true;
        }

        return false;
    }

    private static JsonNode ParseGeminiToolArguments(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return new JsonObject();
        }

        try
        {
            return JsonNode.Parse(arguments) is JsonObject obj ? obj : new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    private static string ResolveGeminiResponseModel(string model, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(model))
        {
            return model.Trim();
        }

        return string.IsNullOrWhiteSpace(fallback) ? "gemini-2.5-pro" : fallback.Trim();
    }
}
