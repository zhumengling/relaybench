using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.IO;
using RelayBench.Core.Services;
using RelayBench.Core.Support;

namespace RelayBench.Services;

internal sealed partial class TransparentProxyResponseForwarderService
{
    private async Task CopyNormalizedChatJsonAsync(
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

        var normalizedBytes = normalized.Body;
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

    private async Task CopyNormalizedChatJsonAsStreamAsync(
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
        var model = string.IsNullOrWhiteSpace(responseModel) ? "relaybench-proxy" : responseModel.Trim();
        var streamId = $"chatcmpl-relaybench-{Guid.NewGuid():N}";

        if (!string.IsNullOrWhiteSpace(normalized.ReasoningText))
        {
            await WriteSseDataAsync(
                context.Response.OutputStream,
                _responseNormalizer.BuildOpenAiChatReasoningChunk(normalized.ReasoningText, model, wireApi, streamId),
                cancellationToken);
        }

        foreach (var chunk in SplitSyntheticStreamChunks(normalized.AssistantText))
        {
            await WriteSseDataAsync(
                context.Response.OutputStream,
                _responseNormalizer.BuildOpenAiChatCompletionChunk(chunk, model, wireApi, streamId),
                cancellationToken);
            TrackOutputTextTokens(chunk);
        }

        for (var index = 0; index < normalized.Images.Count; index++)
        {
            await WriteSseDataAsync(
                context.Response.OutputStream,
                _responseNormalizer.BuildOpenAiChatImageChunk(index, normalized.Images[index], model, wireApi, streamId),
                cancellationToken);
        }

        for (var index = 0; index < normalized.ToolCalls.Count; index++)
        {
            var toolCall = normalized.ToolCalls[index];
            await WriteSseDataAsync(
                context.Response.OutputStream,
                _responseNormalizer.BuildOpenAiChatToolCallChunk(
                    index,
                    toolCall.Id,
                    toolCall.Name,
                    string.IsNullOrWhiteSpace(toolCall.Arguments) ? "{}" : toolCall.Arguments,
                    model,
                    wireApi,
                    streamId),
                cancellationToken);
        }

        var finishReason = normalized.ToolCalls.Count > 0 ? "tool_calls" : "stop";
        await WriteSseDataAsync(
            context.Response.OutputStream,
            _responseNormalizer.BuildOpenAiChatCompletionTerminalChunk(
                model,
                wireApi,
                streamId,
                normalized.AssistantText,
                finishReason,
                usage: normalized.Usage),
            cancellationToken);
        await WriteSseDataAsync(context.Response.OutputStream, "[DONE]", cancellationToken);
        context.Response.OutputStream.Close();
    }

    private async Task CopyNormalizedChatEventStreamAsJsonAsync(
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
        var aggregateHasVisibleContent =
            !string.IsNullOrEmpty(aggregate.AssistantText) ||
            !string.IsNullOrWhiteSpace(aggregate.ReasoningText) ||
            aggregate.Images.Count > 0;
        var normalizedBytes = aggregate.ToolCalls.Count > 0 && !aggregateHasVisibleContent
            ? _responseNormalizer.BuildOpenAiChatToolCallJson(
                aggregate.ToolCalls,
                aggregate.Model,
                wireApi,
                aggregate.Usage,
                aggregate.ReasoningText)
            : _responseNormalizer.BuildOpenAiChatCompletionJson(
                aggregate.AssistantText,
                aggregate.Model,
                wireApi,
                aggregate.Usage,
                aggregate.Images,
                aggregate.ReasoningText,
                aggregate.ToolCalls.Count > 0 ? aggregate.ToolCalls : null);

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

    internal static TransparentProxyChatSseAggregate BuildChatAggregateFromSseEvents(
        IEnumerable<TransparentProxySseEvent> events,
        string responseModel,
        string wireApi,
        IReadOnlyDictionary<string, string>? toolNameAliases,
        TransparentProxyResponseNormalizationService responseNormalizer)
    {
        var model = string.IsNullOrWhiteSpace(responseModel) ? string.Empty : responseModel.Trim();
        StringBuilder deltaText = new();
        StringBuilder reasoningText = new();
        StringBuilder fallbackText = new();
        List<TransparentProxyResponseNormalizationService.TransparentProxyNormalizedImage> images = [];
        JsonNode? usage = null;
        var toolCallNormalizer = new TransparentProxyChatToolCallStreamNormalizer(
            responseNormalizer,
            string.IsNullOrWhiteSpace(model) ? "relaybench-proxy" : model,
            wireApi,
            "chatcmpl-relaybench-aggregate",
            toolNameAliases);

        foreach (var sseEvent in events)
        {
            var data = sseEvent.Data;
            if (string.IsNullOrWhiteSpace(data) ||
                string.Equals(data.Trim(), "[DONE]", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var toolCallChunks = toolCallNormalizer.BuildChunks(sseEvent);
            images.AddRange(TransparentProxyResponseNormalizationService.ExtractInlineDataImages(data));
            usage = TransparentProxyResponseNormalizationService.TryExtractUsageNode(data) ?? usage;
            if (string.IsNullOrWhiteSpace(model))
            {
                model = ResolveChatAggregateModel(data);
            }

            var reasoningDelta = ChatSseParser.TryExtractReasoningDelta(data);
            if (!string.IsNullOrEmpty(reasoningDelta))
            {
                reasoningText.Append(reasoningDelta);
            }

            if (ShouldSuppressVisibleDeltaAfterChatToolUseEvent(sseEvent, toolCallChunks.Count))
            {
                continue;
            }

            var delta = ChatSseParser.TryExtractDelta(data);
            if (!string.IsNullOrEmpty(delta))
            {
                deltaText.Append(delta);
                continue;
            }

            if (IsAggregateFallbackEvent(sseEvent) &&
                TryExtractAssistantTextFromSseData(data) is { Length: > 0 } fallback)
            {
                fallbackText.Append(fallback);
            }
        }

        var assistantText = deltaText.Length > 0 ? deltaText.ToString() : fallbackText.ToString();
        return new TransparentProxyChatSseAggregate(
            assistantText,
            string.IsNullOrWhiteSpace(model) ? "relaybench-proxy" : model,
            usage,
            reasoningText.ToString(),
            toolCallNormalizer.BuildToolCalls(),
            images);
    }

    private static string ResolveChatAggregateModel(string data)
    {
        try
        {
            using var document = JsonDocument.Parse(data);
            return TryReadStringPath(document.RootElement, "model") ??
                   TryReadStringPath(document.RootElement, "response", "model") ??
                   TryReadStringPath(document.RootElement, "modelVersion") ??
                   TryReadStringPath(document.RootElement, "response", "modelVersion") ??
                   string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private async Task CopyNormalizedChatStreamAsync(
        HttpListenerContext context,
        HttpResponseMessage upstreamResponse,
        string responseModel,
        string wireApi,
        bool preferJsonStreamExtraction,
        IReadOnlyDictionary<string, string>? toolNameAliases,
        CancellationToken cancellationToken)
    {
        context.Response.ContentType = "text/event-stream; charset=utf-8";
        context.Response.SendChunked = true;
        var model = string.IsNullOrWhiteSpace(responseModel) ? "relaybench-proxy" : responseModel.Trim();
        var streamId = $"chatcmpl-relaybench-{Guid.NewGuid():N}";
        var toolCallStreamNormalizer = new TransparentProxyChatToolCallStreamNormalizer(
            _responseNormalizer,
            model,
            wireApi,
            streamId,
            toolNameAliases);
        var wroteDone = false;
        var wroteTerminalChunk = false;
        var imageIndex = 0;
        JsonNode? streamUsage = null;
        StringBuilder assistantText = new();

        async Task WriteBufferedJsonChunkIfNeededAsync()
        {
            if (!preferJsonStreamExtraction || assistantText.Length == 0)
            {
                return;
            }

            var original = assistantText.ToString();
            var extracted = TryExtractFirstJsonObject(original);
            if (string.IsNullOrWhiteSpace(extracted))
            {
                return;
            }

            assistantText.Clear();
            assistantText.Append(extracted);
            var chunk = _responseNormalizer.BuildOpenAiChatCompletionChunk(extracted, model, wireApi, streamId);
            await WriteSseDataAsync(context.Response.OutputStream, chunk, cancellationToken);
        }

        async Task WriteTerminalChunkAsync()
        {
            if (wroteTerminalChunk)
            {
                return;
            }

            await WriteBufferedJsonChunkIfNeededAsync();
            var terminalChunk = _responseNormalizer.BuildOpenAiChatCompletionTerminalChunk(
                model,
                wireApi,
                streamId,
                assistantText.ToString(),
                toolCallStreamNormalizer.HasToolCalls ? "tool_calls" : "stop",
                streamUsage);
            await WriteSseDataAsync(context.Response.OutputStream, terminalChunk, cancellationToken);
            wroteTerminalChunk = true;
        }

        await foreach (var sseEvent in _sseFramer.ReadEventsAsync(upstreamResponse.Content, cancellationToken))
        {
            var data = sseEvent.Data;
            if (TransparentProxyResponseNormalizationService.TryExtractUsageNode(data) is { } usage)
            {
                streamUsage = usage;
            }

            if (ChatSseParser.IsDone(data))
            {
                await WriteTerminalChunkAsync();
                await WriteSseDataAsync(context.Response.OutputStream, "[DONE]", cancellationToken);
                TrackPromptCacheTokens(data);
                wroteDone = true;
                break;
            }

            var toolCallChunks = toolCallStreamNormalizer.BuildChunks(sseEvent);
            var reasoningDelta = ChatSseParser.TryExtractReasoningDelta(data);
            if (!string.IsNullOrEmpty(reasoningDelta))
            {
                await WriteSseDataAsync(
                    context.Response.OutputStream,
                    _responseNormalizer.BuildOpenAiChatReasoningChunk(reasoningDelta, model, wireApi, streamId),
                    cancellationToken);
            }

            if (toolCallChunks.Count > 0)
            {
                foreach (var toolCallChunk in toolCallChunks)
                {
                    await WriteSseDataAsync(context.Response.OutputStream, toolCallChunk, cancellationToken);
                }

                if (ShouldSuppressVisibleDeltaAfterChatToolUseEvent(sseEvent, toolCallChunks.Count))
                {
                    TrackPromptCacheTokens(data);
                    continue;
                }
            }

            var imageParts = TransparentProxyResponseNormalizationService.ExtractInlineDataImages(data);
            if (imageParts.Count > 0)
            {
                foreach (var image in imageParts)
                {
                    await WriteSseDataAsync(
                        context.Response.OutputStream,
                        _responseNormalizer.BuildOpenAiChatImageChunk(imageIndex++, image, model, wireApi, streamId),
                        cancellationToken);
                }
            }

            var delta = ChatSseParser.TryExtractDelta(data);
            if (string.IsNullOrEmpty(delta))
            {
                TrackPromptCacheTokens(data);
                continue;
            }

            var chunk = _responseNormalizer.BuildOpenAiChatCompletionChunk(delta, model, wireApi, streamId);
            assistantText.Append(delta);
            if (!preferJsonStreamExtraction)
            {
                await WriteSseDataAsync(context.Response.OutputStream, chunk, cancellationToken);
            }

            TrackOutputTextTokens(delta);
            TrackPromptCacheTokens(data);
        }

        await WriteTerminalChunkAsync();
        if (!wroteDone)
        {
            await WriteSseDataAsync(context.Response.OutputStream, "[DONE]", cancellationToken);
        }

        context.Response.OutputStream.Close();
    }
}
