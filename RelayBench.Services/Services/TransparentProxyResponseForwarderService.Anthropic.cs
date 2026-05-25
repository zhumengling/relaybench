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
    private async Task CopyNormalizedAnthropicJsonAsync(
        HttpListenerContext context,
        HttpResponseMessage upstreamResponse,
        int statusCode,
        string cacheKey,
        TransparentProxyServerConfig config,
        string responseModel,
        string logModel,
        string wireApi,
        CancellationToken cancellationToken)
    {
        var upstreamBytes = await upstreamResponse.Content.ReadAsByteArrayAsync(cancellationToken);
        var upstreamText = Encoding.UTF8.GetString(upstreamBytes);
        TrackPromptCacheTokens(upstreamText);
        var assistantText = ModelResponseTextExtractor.TryExtractAssistantText(upstreamText) ?? string.Empty;
        var normalizedBytes = BuildAnthropicMessageBytes(
            assistantText,
            ResolveAnthropicModel(responseModel, upstreamText),
            wireApi,
            upstreamText);
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

    private async Task CopyNormalizedAnthropicStreamAsync(
        HttpListenerContext context,
        HttpResponseMessage upstreamResponse,
        string responseModel,
        string wireApi,
        CancellationToken cancellationToken)
    {
        context.Response.ContentType = "text/event-stream; charset=utf-8";
        context.Response.SendChunked = true;
        var model = string.IsNullOrWhiteSpace(responseModel) ? "relaybench-proxy" : responseModel.Trim();
        var messageId = $"msg_relaybench_{Guid.NewGuid():N}";
        var wroteStop = false;
        StringBuilder assistantText = new();
        var finalOutputTokens = 0;
        var finalUsage = TransparentProxyAnthropicUsage.Empty;
        var toolUseNormalizer = new TransparentProxyAnthropicToolUseStreamNormalizer();
        var textContentBlockIndex = -1;
        var textContentBlockStarted = false;
        var textContentBlockStopped = false;
        var thinkingContentBlockIndex = -1;
        var thinkingContentBlockStarted = false;
        var thinkingContentBlockStopped = false;
        var thinkingStopPending = false;
        var thinkingSummarySeen = false;
        var thinkingSignature = string.Empty;
        var finalStopReason = "end_turn";
        string? finalStopSequence = null;

        await WriteAnthropicSseEventAsync(
            context.Response.OutputStream,
            "message_start",
            BuildAnthropicMessageStartEvent(messageId, model),
            cancellationToken);

        async Task EnsureTextContentBlockStartedAsync()
        {
            if (textContentBlockStarted && !textContentBlockStopped)
            {
                return;
            }

            await StopThinkingContentBlockAsync(startIfSignatureOnly: false);
            textContentBlockIndex = toolUseNormalizer.AllocateContentBlockIndex();
            textContentBlockStarted = true;
            textContentBlockStopped = false;
            await WriteAnthropicSseEventAsync(
                context.Response.OutputStream,
                "content_block_start",
                BuildAnthropicTextContentBlockStartEvent(textContentBlockIndex),
                cancellationToken);
        }

        async Task EnsureThinkingContentBlockStartedAsync()
        {
            if (thinkingContentBlockStarted && !thinkingContentBlockStopped)
            {
                return;
            }

            await StopTextContentBlockAsync();
            thinkingContentBlockIndex = toolUseNormalizer.AllocateContentBlockIndex();
            thinkingContentBlockStarted = true;
            thinkingContentBlockStopped = false;
            await WriteAnthropicSseEventAsync(
                context.Response.OutputStream,
                "content_block_start",
                BuildAnthropicThinkingContentBlockStartEvent(thinkingContentBlockIndex),
                cancellationToken);
        }

        async Task StopThinkingContentBlockAsync(bool startIfSignatureOnly)
        {
            if ((!thinkingContentBlockStarted || thinkingContentBlockStopped) &&
                (!startIfSignatureOnly || string.IsNullOrWhiteSpace(thinkingSignature)))
            {
                return;
            }

            if (!thinkingContentBlockStarted || thinkingContentBlockStopped)
            {
                await EnsureThinkingContentBlockStartedAsync();
            }

            if (!string.IsNullOrWhiteSpace(thinkingSignature))
            {
                await WriteAnthropicSseEventAsync(
                    context.Response.OutputStream,
                    "content_block_delta",
                    BuildAnthropicSignatureDeltaEvent(thinkingSignature, thinkingContentBlockIndex),
                    cancellationToken);
            }

            thinkingContentBlockStopped = true;
            thinkingStopPending = false;
            await WriteAnthropicSseEventAsync(
                context.Response.OutputStream,
                "content_block_stop",
                BuildAnthropicContentBlockStopEvent(thinkingContentBlockIndex),
                cancellationToken);
        }

        async Task StopTextContentBlockAsync()
        {
            if (!textContentBlockStarted || textContentBlockStopped)
            {
                return;
            }

            textContentBlockStopped = true;
            await WriteAnthropicSseEventAsync(
                context.Response.OutputStream,
                "content_block_stop",
                BuildAnthropicContentBlockStopEvent(textContentBlockIndex),
                cancellationToken);
        }

        async Task WriteToolUseEventsAsync(IReadOnlyList<TransparentProxySseEvent> toolUseEvents)
        {
            if (toolUseEvents.Count == 0)
            {
                return;
            }

            await StopTextContentBlockAsync();
            await StopThinkingContentBlockAsync(startIfSignatureOnly: false);
            foreach (var toolUseEvent in toolUseEvents)
            {
                await WriteAnthropicSseEventAsync(
                    context.Response.OutputStream,
                    toolUseEvent.EventName,
                    toolUseEvent.Data,
                    cancellationToken);
            }
        }

        async Task WriteStopEventsAsync()
        {
            if (wroteStop)
            {
                return;
            }

            wroteStop = true;
            await StopThinkingContentBlockAsync(startIfSignatureOnly: false);
            await StopTextContentBlockAsync();
            await WriteToolUseEventsAsync(toolUseNormalizer.FlushOpenBlocks());
            var outputTokens = finalOutputTokens > 0
                ? finalOutputTokens
                : Math.Max(0, TokenCountEstimator.EstimateOutputTokens(assistantText.ToString()));
            var usage = finalUsage.WithOutputFallback(outputTokens);
            var effectiveStopReason = toolUseNormalizer.HasToolCalls ? "tool_use" : finalStopReason;
            await WriteAnthropicSseEventAsync(
                context.Response.OutputStream,
                "message_delta",
                BuildAnthropicMessageDeltaEvent(
                    usage,
                    effectiveStopReason,
                    toolUseNormalizer.HasToolCalls ? null : finalStopSequence),
                cancellationToken);
            await WriteAnthropicSseEventAsync(
                context.Response.OutputStream,
                "message_stop",
                "{\"type\":\"message_stop\"}",
                cancellationToken);
        }

        await foreach (var sseEvent in _sseFramer.ReadEventsAsync(upstreamResponse.Content, cancellationToken))
        {
            var data = sseEvent.Data;
            if (ChatSseParser.TryExtractOutputTokenCount(data, out var upstreamOutputTokens))
            {
                finalOutputTokens = upstreamOutputTokens;
            }

            finalUsage = finalUsage.Merge(ResolveAnthropicUsage(data, null));
            if (TryResolveAnthropicStopMetadata(data, out var eventStopReason, out var eventStopSequence))
            {
                finalStopReason = eventStopReason;
                finalStopSequence = eventStopSequence;
            }

            if (TryReadResponsesStreamEvent(data, out var responseEventType, out var responseItemType, out var responseReasoningSignature) &&
                thinkingContentBlockStarted &&
                !thinkingContentBlockStopped &&
                thinkingStopPending &&
                (string.Equals(responseEventType, "response.content_part.added", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(responseEventType, "response.completed", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(responseEventType, "response.incomplete", StringComparison.OrdinalIgnoreCase)))
            {
                await StopThinkingContentBlockAsync(startIfSignatureOnly: false);
            }

            if (string.Equals(responseEventType, "response.reasoning_summary_part.added", StringComparison.OrdinalIgnoreCase))
            {
                if (thinkingStopPending)
                {
                    await StopThinkingContentBlockAsync(startIfSignatureOnly: false);
                }

                thinkingSummarySeen = true;
                await EnsureThinkingContentBlockStartedAsync();
                TrackPromptCacheTokens(data);
                continue;
            }

            var reasoningDelta = ChatSseParser.TryExtractReasoningDelta(data);
            if (!string.IsNullOrEmpty(reasoningDelta))
            {
                await EnsureThinkingContentBlockStartedAsync();
                await WriteAnthropicSseEventAsync(
                    context.Response.OutputStream,
                    "content_block_delta",
                    BuildAnthropicThinkingDeltaEvent(reasoningDelta, thinkingContentBlockIndex),
                    cancellationToken);
                TrackPromptCacheTokens(data);
                continue;
            }

            if (string.Equals(responseEventType, "response.reasoning_summary_part.done", StringComparison.OrdinalIgnoreCase))
            {
                thinkingStopPending = true;
                TrackPromptCacheTokens(data);
                continue;
            }

            if (string.Equals(responseEventType, "response.output_item.added", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(responseItemType, "reasoning", StringComparison.OrdinalIgnoreCase))
            {
                thinkingSummarySeen = false;
                if (!string.IsNullOrWhiteSpace(responseReasoningSignature))
                {
                    thinkingSignature = responseReasoningSignature;
                }

                TrackPromptCacheTokens(data);
                continue;
            }

            if (string.Equals(responseEventType, "response.output_item.done", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(responseItemType, "reasoning", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(responseReasoningSignature))
                {
                    thinkingSignature = responseReasoningSignature;
                }

                await StopThinkingContentBlockAsync(startIfSignatureOnly: !thinkingSummarySeen);
                thinkingSignature = string.Empty;
                thinkingSummarySeen = false;
                TrackPromptCacheTokens(data);
                continue;
            }

            var toolUseEvents = toolUseNormalizer.ExtractToolUseEvents(data);
            await WriteToolUseEventsAsync(toolUseEvents);
            if (ShouldSuppressVisibleDeltaAfterAnthropicToolUseEvent(
                    responseEventType,
                    responseItemType,
                    toolUseEvents.Count))
            {
                TrackPromptCacheTokens(data);
                continue;
            }

            if (ChatSseParser.IsDone(data))
            {
                await WriteStopEventsAsync();
                break;
            }

            var delta = ChatSseParser.TryExtractDelta(data);
            if (string.IsNullOrEmpty(delta))
            {
                TrackPromptCacheTokens(data);
                continue;
            }

            assistantText.Append(delta);
            await EnsureTextContentBlockStartedAsync();
            await WriteAnthropicSseEventAsync(
                context.Response.OutputStream,
                "content_block_delta",
                BuildAnthropicContentDeltaEvent(delta, textContentBlockIndex),
                cancellationToken);
            TrackOutputTextTokens(delta);
            TrackPromptCacheTokens(data);
        }

        await WriteStopEventsAsync();
        context.Response.OutputStream.Close();
    }

    private async Task CopyNormalizedAnthropicEventStreamAsJsonAsync(
        HttpListenerContext context,
        HttpResponseMessage upstreamResponse,
        int statusCode,
        string cacheKey,
        TransparentProxyServerConfig config,
        string responseModel,
        string logModel,
        string wireApi,
        CancellationToken cancellationToken)
    {
        List<TransparentProxySseEvent> events = [];
        await foreach (var sseEvent in _sseFramer.ReadEventsAsync(upstreamResponse.Content, cancellationToken))
        {
            events.Add(sseEvent);
            TrackPromptCacheTokens(sseEvent.Data);
        }

        var aggregate = BuildAnthropicAggregateFromSseEvents(events, responseModel);
        var normalizedBytes = aggregate.Content is { Count: > 0 } content
            ? BuildAnthropicMessageBytes(
                content,
                aggregate.Model,
                wireApi,
                aggregate.Usage,
                aggregate.StopReason,
                aggregate.StopSequence)
            : BuildAnthropicMessageBytes(
                aggregate.AssistantText,
                aggregate.Model,
                wireApi,
                aggregate.Usage,
                aggregate.StopReason,
                aggregate.StopSequence);
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

    private async Task CopyNormalizedAnthropicJsonAsStreamAsync(
        HttpListenerContext context,
        HttpResponseMessage upstreamResponse,
        string responseModel,
        string wireApi,
        CancellationToken cancellationToken)
    {
        var upstreamBytes = await upstreamResponse.Content.ReadAsByteArrayAsync(cancellationToken);
        var upstreamText = Encoding.UTF8.GetString(upstreamBytes);
        TrackPromptCacheTokens(upstreamText);
        var assistantText = ModelResponseTextExtractor.TryExtractAssistantText(upstreamText) ?? string.Empty;
        var model = ResolveAnthropicModel(responseModel, upstreamText);
        var content = BuildAnthropicContentBlocks(upstreamText, assistantText, out var stopReason, out var stopSequence);
        var messageId = $"msg_relaybench_{Guid.NewGuid():N}";

        context.Response.ContentType = "text/event-stream; charset=utf-8";
        context.Response.SendChunked = true;
        await WriteAnthropicSseEventAsync(
            context.Response.OutputStream,
            "message_start",
            BuildAnthropicMessageStartEvent(messageId, model),
            cancellationToken);
        await WriteAnthropicJsonContentBlocksAsStreamAsync(
            context.Response.OutputStream,
            content,
            cancellationToken);
        await WriteAnthropicSseEventAsync(
            context.Response.OutputStream,
            "message_delta",
            BuildAnthropicMessageDeltaEvent(
                ResolveAnthropicUsage(upstreamText, assistantText),
                stopReason,
                stopSequence),
            cancellationToken);
        await WriteAnthropicSseEventAsync(
            context.Response.OutputStream,
            "message_stop",
            "{\"type\":\"message_stop\"}",
            cancellationToken);
        context.Response.OutputStream.Close();
    }

    private async Task WriteAnthropicJsonContentBlocksAsStreamAsync(
        Stream outputStream,
        JsonArray content,
        CancellationToken cancellationToken)
    {
        var blockIndex = 0;
        foreach (var node in content)
        {
            if (node is not JsonObject block)
            {
                continue;
            }

            var type = ReadAnthropicContentBlockString(block, "type");
            if (string.Equals(type, "text", StringComparison.OrdinalIgnoreCase))
            {
                await WriteAnthropicTextBlockAsStreamAsync(
                    outputStream,
                    ReadAnthropicContentBlockString(block, "text"),
                    blockIndex,
                    cancellationToken);
                blockIndex++;
                continue;
            }

            if (string.Equals(type, "thinking", StringComparison.OrdinalIgnoreCase))
            {
                await WriteAnthropicThinkingBlockAsStreamAsync(
                    outputStream,
                    block,
                    blockIndex,
                    cancellationToken);
                blockIndex++;
                continue;
            }

            if (string.Equals(type, "tool_use", StringComparison.OrdinalIgnoreCase))
            {
                await WriteAnthropicToolUseBlockAsStreamAsync(
                    outputStream,
                    block,
                    blockIndex,
                    cancellationToken);
                blockIndex++;
            }
        }

        if (blockIndex == 0)
        {
            await WriteAnthropicTextBlockAsStreamAsync(
                outputStream,
                string.Empty,
                0,
                cancellationToken);
        }
    }

    private async Task WriteAnthropicTextBlockAsStreamAsync(
        Stream outputStream,
        string text,
        int index,
        CancellationToken cancellationToken)
    {
        await WriteAnthropicSseEventAsync(
            outputStream,
            "content_block_start",
            BuildAnthropicTextContentBlockStartEvent(index),
            cancellationToken);
        foreach (var chunk in SplitSyntheticStreamChunks(text))
        {
            await WriteAnthropicSseEventAsync(
                outputStream,
                "content_block_delta",
                BuildAnthropicContentDeltaEvent(chunk, index),
                cancellationToken);
            TrackOutputTextTokens(chunk);
        }

        await WriteAnthropicSseEventAsync(
            outputStream,
            "content_block_stop",
            BuildAnthropicContentBlockStopEvent(index),
            cancellationToken);
    }

    private static async Task WriteAnthropicThinkingBlockAsStreamAsync(
        Stream outputStream,
        JsonObject block,
        int index,
        CancellationToken cancellationToken)
    {
        await WriteAnthropicSseEventAsync(
            outputStream,
            "content_block_start",
            BuildAnthropicThinkingContentBlockStartEvent(index),
            cancellationToken);
        var thinking = ReadAnthropicContentBlockString(block, "thinking");
        if (!string.IsNullOrEmpty(thinking))
        {
            await WriteAnthropicSseEventAsync(
                outputStream,
                "content_block_delta",
                BuildAnthropicThinkingDeltaEvent(thinking, index),
                cancellationToken);
        }

        var signature = ReadAnthropicContentBlockString(block, "signature");
        if (!string.IsNullOrWhiteSpace(signature))
        {
            await WriteAnthropicSseEventAsync(
                outputStream,
                "content_block_delta",
                BuildAnthropicSignatureDeltaEvent(signature, index),
                cancellationToken);
        }

        await WriteAnthropicSseEventAsync(
            outputStream,
            "content_block_stop",
            BuildAnthropicContentBlockStopEvent(index),
            cancellationToken);
    }

    private static async Task WriteAnthropicToolUseBlockAsStreamAsync(
        Stream outputStream,
        JsonObject block,
        int index,
        CancellationToken cancellationToken)
    {
        await WriteAnthropicSseEventAsync(
            outputStream,
            "content_block_start",
            BuildAnthropicToolUseContentBlockStartEvent(
                index,
                ReadAnthropicContentBlockString(block, "id"),
                ReadAnthropicContentBlockString(block, "name")),
            cancellationToken);
        await WriteAnthropicSseEventAsync(
            outputStream,
            "content_block_delta",
            BuildAnthropicToolUseInputDeltaEvent(ReadAnthropicToolInputJson(block), index),
            cancellationToken);
        await WriteAnthropicSseEventAsync(
            outputStream,
            "content_block_stop",
            BuildAnthropicContentBlockStopEvent(index),
            cancellationToken);
    }

    private static string ReadAnthropicContentBlockString(JsonObject block, string propertyName)
        => block.TryGetPropertyValue(propertyName, out var node) &&
           node is JsonValue value &&
           value.TryGetValue<string>(out var text)
            ? text ?? string.Empty
            : string.Empty;

    private static string ReadAnthropicToolInputJson(JsonObject block)
        => block.TryGetPropertyValue("input", out var input) && input is not null
            ? input.ToJsonString()
            : "{}";
}
