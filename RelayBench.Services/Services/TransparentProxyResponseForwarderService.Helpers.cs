using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.IO;
using RelayBench.Core.Services;
using RelayBench.Core.Support;

namespace RelayBench.Services;

internal sealed partial class TransparentProxyResponseForwarderService
{
    private void TrackOutputTextTokens(string? text)
    {
        if (_tokenTelemetry.TrackOutputText(text))
        {
            _publishMetrics();
        }
    }

    private void TrackPromptCacheTokens(string? json)
    {
        if (_tokenTelemetry.TrackPromptCache(json))
        {
            _publishMetrics();
        }
    }

    private static bool IsEventStreamContentType(string contentType)
        => contentType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase);

    private static bool ShouldCaptureResponseBodyForTokenTelemetry(int statusCode, string contentType)
    {
        if (statusCode < 200 || statusCode >= 300)
        {
            return false;
        }

        return contentType.Contains("json", StringComparison.OrdinalIgnoreCase) ||
               contentType.Contains("text/plain", StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryExtractFirstJsonObject(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var start = text.IndexOf('{');
        if (start < 0)
        {
            return null;
        }

        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var index = start; index < text.Length; index++)
        {
            var character = text[index];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (character == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (character == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (character == '"')
            {
                inString = true;
                continue;
            }

            if (character == '{')
            {
                depth++;
                continue;
            }

            if (character != '}')
            {
                continue;
            }

            depth--;
            if (depth != 0)
            {
                continue;
            }

            var candidate = text[start..(index + 1)].Trim();
            try
            {
                using var _ = JsonDocument.Parse(candidate);
                return candidate;
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private static async Task WriteSseDataAsync(Stream outputStream, string data, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes($"data: {data}\n\n");
        await outputStream.WriteAsync(bytes, cancellationToken);
        await outputStream.FlushAsync(cancellationToken);
    }

    private static IEnumerable<string> SplitSyntheticStreamChunks(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield break;
        }

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

    private static async Task WriteAnthropicSseEventAsync(
        Stream outputStream,
        string eventName,
        string data,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes($"event: {eventName}\ndata: {data}\n\n");
        await outputStream.WriteAsync(bytes, cancellationToken);
        await outputStream.FlushAsync(cancellationToken);
    }

    internal static byte[] BuildAnthropicMessageBytes(
        string assistantText,
        string model,
        string wireApi,
        string upstreamText)
    {
        var usage = ResolveAnthropicUsage(upstreamText, assistantText);
        var content = BuildAnthropicContentBlocks(upstreamText, assistantText, out var stopReason, out var stopSequence);
        return BuildAnthropicMessageBytes(content, model, wireApi, usage, stopReason, stopSequence);
    }

    internal static byte[] BuildAnthropicMessageBytes(
        string assistantText,
        string model,
        string wireApi,
        TransparentProxyAnthropicUsage usage)
        => BuildAnthropicMessageBytes(
            BuildFallbackAnthropicTextContent(assistantText),
            model,
            wireApi,
            usage,
            "end_turn",
            stopSequence: null);

    internal static byte[] BuildAnthropicMessageBytes(
        string assistantText,
        string model,
        string wireApi,
        TransparentProxyAnthropicUsage usage,
        string stopReason,
        string? stopSequence)
        => BuildAnthropicMessageBytes(
            BuildFallbackAnthropicTextContent(assistantText),
            model,
            wireApi,
            usage,
            stopReason,
            stopSequence);

    private static byte[] BuildAnthropicMessageBytes(
        JsonArray content,
        string model,
        string wireApi,
        TransparentProxyAnthropicUsage usage,
        string stopReason,
        string? stopSequence)
    {
        var root = new JsonObject
        {
            ["id"] = $"msg_relaybench_{Guid.NewGuid():N}",
            ["type"] = "message",
            ["role"] = "assistant",
            ["model"] = model,
            ["content"] = content,
            ["stop_reason"] = stopReason,
            ["stop_sequence"] = string.IsNullOrEmpty(stopSequence) ? null : stopSequence,
            ["usage"] = JsonSerializer.SerializeToNode(usage.EnsureMessageUsage()),
            ["relaybench"] = new JsonObject
            {
                ["upstream_wire_api"] = wireApi
            }
        };
        return JsonSerializer.SerializeToUtf8Bytes(root);
    }

    private static JsonArray BuildFallbackAnthropicTextContent(string? assistantText)
        => new()
        {
            new JsonObject
            {
                ["type"] = "text",
                ["text"] = assistantText ?? string.Empty
            }
        };

    private static JsonArray BuildAnthropicContentBlocks(
        string? upstreamText,
        string? assistantText,
        out string stopReason,
        out string? stopSequence)
    {
        stopReason = "end_turn";
        stopSequence = null;
        if (string.IsNullOrWhiteSpace(upstreamText))
        {
            return BuildFallbackAnthropicTextContent(assistantText);
        }

        try
        {
            using var document = JsonDocument.Parse(upstreamText);
            var source = document.RootElement;
            if (source.ValueKind == JsonValueKind.Object &&
                source.TryGetProperty("response", out var response) &&
                response.ValueKind == JsonValueKind.Object)
            {
                source = response;
            }

            var content = new JsonArray();
            AppendResponsesOutputAsAnthropicContent(source, content);
            AppendChatToolCallsAsAnthropicContent(source, content);
            var hasToolUse = HasAnthropicToolUse(content);
            stopReason = ResolveAnthropicStopReason(source, hasToolUse);
            stopSequence = hasToolUse ? null : ResolveAnthropicStopSequence(source);
            if (content.Count > 0)
            {
                return content;
            }
        }
        catch (JsonException)
        {
        }

        return BuildFallbackAnthropicTextContent(assistantText);
    }

    private static void AppendResponsesOutputAsAnthropicContent(JsonElement root, JsonArray content)
    {
        if (!root.TryGetProperty("output", out var output) ||
            output.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in output.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("type", out var typeElement) ||
                typeElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var type = typeElement.GetString();
            if (string.Equals(type, "message", StringComparison.OrdinalIgnoreCase))
            {
                AppendMessageContentPartsAsAnthropicText(item, content);
                continue;
            }

            if (string.Equals(type, "function_call", StringComparison.OrdinalIgnoreCase))
            {
                content.Add(BuildAnthropicToolUseBlock(
                    ReadStringProperty(item, "call_id", "id"),
                    ReadStringProperty(item, "name"),
                    ReadStringProperty(item, "arguments")));
                continue;
            }

            if (string.Equals(type, "reasoning", StringComparison.OrdinalIgnoreCase) &&
                TryBuildAnthropicThinkingBlock(item, out var thinkingBlock))
            {
                content.Add(thinkingBlock);
            }
        }
    }

    private static void AppendMessageContentPartsAsAnthropicText(JsonElement message, JsonArray content)
    {
        if (!message.TryGetProperty("content", out var parts) ||
            parts.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var part in parts.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object ||
                !part.TryGetProperty("text", out var textElement) ||
                textElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            if (part.TryGetProperty("type", out var partType) &&
                partType.ValueKind == JsonValueKind.String &&
                !string.Equals(partType.GetString(), "output_text", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(partType.GetString(), "text", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            content.Add(new JsonObject
            {
                ["type"] = "text",
                ["text"] = textElement.GetString() ?? string.Empty
            });
        }
    }

    private static void AppendChatToolCallsAsAnthropicContent(JsonElement root, JsonArray content)
    {
        if (!root.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var choice in choices.EnumerateArray())
        {
            if (choice.TryGetProperty("message", out var message))
            {
                AppendChatMessageTextAsAnthropicContent(message, content);
                AppendChatToolCallsFromContainer(message, content);
            }

            if (choice.TryGetProperty("delta", out var delta))
            {
                AppendChatMessageTextAsAnthropicContent(delta, content);
                AppendChatToolCallsFromContainer(delta, content);
            }
        }
    }

    private static void AppendChatMessageTextAsAnthropicContent(JsonElement message, JsonArray content)
    {
        if (!message.TryGetProperty("content", out var contentElement))
        {
            return;
        }

        if (contentElement.ValueKind == JsonValueKind.String)
        {
            var text = contentElement.GetString();
            if (!string.IsNullOrEmpty(text))
            {
                content.Add(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = text
                });
            }

            return;
        }

        if (contentElement.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var part in contentElement.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.Object &&
                part.TryGetProperty("type", out var type) &&
                type.ValueKind == JsonValueKind.String &&
                string.Equals(type.GetString(), "text", StringComparison.OrdinalIgnoreCase) &&
                part.TryGetProperty("text", out var text) &&
                text.ValueKind == JsonValueKind.String)
            {
                content.Add(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = text.GetString() ?? string.Empty
                });
            }
        }
    }

    private static void AppendChatToolCallsFromContainer(JsonElement container, JsonArray content)
    {
        if (!container.TryGetProperty("tool_calls", out var toolCalls) ||
            toolCalls.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var toolCall in toolCalls.EnumerateArray())
        {
            if (toolCall.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var function = toolCall.TryGetProperty("function", out var functionElement) &&
                           functionElement.ValueKind == JsonValueKind.Object
                ? functionElement
                : toolCall;
            content.Add(BuildAnthropicToolUseBlock(
                ReadStringProperty(toolCall, "id", "call_id"),
                ReadStringProperty(function, "name"),
                ReadStringProperty(function, "arguments")));
        }
    }

    private static JsonObject BuildAnthropicToolUseBlock(string id, string name, string arguments)
        => new()
        {
            ["type"] = "tool_use",
            ["id"] = TransparentProxyClaudeToolUseId.Normalize(id),
            ["name"] = name,
            ["input"] = ParseAnthropicToolInput(arguments)
        };

    private static JsonNode ParseAnthropicToolInput(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return new JsonObject();
        }

        try
        {
            var node = JsonNode.Parse(arguments);
            return node is JsonObject obj ? obj : new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    private static bool TryBuildAnthropicThinkingBlock(JsonElement item, out JsonObject block)
    {
        var thinking = ExtractResponsesReasoningSummary(item);
        var signature = ReadStringProperty(item, "encrypted_content", "signature");
        block = new JsonObject
        {
            ["type"] = "thinking",
            ["thinking"] = thinking
        };

        if (!string.IsNullOrWhiteSpace(signature))
        {
            block["signature"] = signature;
        }

        return thinking.Length > 0 || !string.IsNullOrWhiteSpace(signature);
    }

    private static string ExtractResponsesReasoningSummary(JsonElement item)
    {
        StringBuilder builder = new();
        if (item.TryGetProperty("summary", out var summary) &&
            summary.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in summary.EnumerateArray())
            {
                AppendResponsesReasoningTextPart(builder, part);
            }
        }
        else if (summary.ValueKind == JsonValueKind.String)
        {
            builder.Append(summary.GetString());
        }

        if (item.TryGetProperty("summary_text", out var summaryText) &&
            summaryText.ValueKind == JsonValueKind.String)
        {
            builder.Append(summaryText.GetString());
        }

        if (item.TryGetProperty("content", out var content) &&
            content.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in content.EnumerateArray())
            {
                AppendResponsesReasoningTextPart(builder, part);
            }
        }
        else if (content.ValueKind == JsonValueKind.String)
        {
            builder.Append(content.GetString());
        }

        if (item.TryGetProperty("text", out var text) &&
            text.ValueKind == JsonValueKind.String)
        {
            builder.Append(text.GetString());
        }

        return builder.ToString();
    }

    private static void AppendResponsesReasoningTextPart(StringBuilder builder, JsonElement part)
    {
        if (part.ValueKind == JsonValueKind.String)
        {
            builder.Append(part.GetString());
            return;
        }

        if (part.ValueKind == JsonValueKind.Object &&
            part.TryGetProperty("text", out var textElement) &&
            textElement.ValueKind == JsonValueKind.String)
        {
            builder.Append(textElement.GetString());
        }
    }

    private static bool HasAnthropicToolUse(JsonArray content)
        => content.Any(static node =>
            node is JsonObject obj &&
            obj.TryGetPropertyValue("type", out var typeNode) &&
            string.Equals(typeNode?.GetValue<string>(), "tool_use", StringComparison.OrdinalIgnoreCase));

    private static string ResolveAnthropicStopReason(JsonElement source, bool hasToolUse)
    {
        if (hasToolUse)
        {
            return "tool_use";
        }

        if (TryReadStringPath(source, "choices", "0", "finish_reason") is { } finishReason)
        {
            return MapOpenAiFinishReasonToAnthropicStopReason(finishReason);
        }

        return MapResponsesStopReasonToAnthropicStopReason(ResolveResponsesStopReason(source));
    }

    private static string MapOpenAiFinishReasonToAnthropicStopReason(string finishReason)
        => finishReason.Trim().ToLowerInvariant() switch
        {
            "length" => "max_tokens",
            "tool_calls" or "function_call" => "tool_use",
            "content_filter" => "refusal",
            _ => "end_turn"
        };

    private static string ResolveResponsesStopReason(JsonElement source)
    {
        var stopReason = ReadStringProperty(source, "stop_reason");
        if (!string.IsNullOrWhiteSpace(stopReason))
        {
            if (string.Equals(stopReason, "stop", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(ResolveAnthropicStopSequence(source)))
            {
                return "stop_sequence";
            }

            return stopReason.Trim();
        }

        var incompleteReason = TryReadStringPath(source, "incomplete_details", "reason");
        if (!string.IsNullOrWhiteSpace(incompleteReason))
        {
            return incompleteReason.Trim();
        }

        return string.IsNullOrEmpty(ResolveAnthropicStopSequence(source))
            ? string.Empty
            : "stop_sequence";
    }

    private static string MapResponsesStopReasonToAnthropicStopReason(string stopReason)
        => stopReason.Trim().ToLowerInvariant() switch
        {
            "" or "stop" or "completed" => "end_turn",
            "length" or "max_tokens" or "max_output_tokens" => "max_tokens",
            "tool_use" or "tool_calls" or "function_call" => "tool_use",
            "end_turn" or "stop_sequence" or "pause_turn" or "refusal" or "model_context_window_exceeded" => stopReason.Trim().ToLowerInvariant(),
            "content_filter" => "refusal",
            _ => "end_turn"
        };

    private static string? ResolveAnthropicStopSequence(JsonElement source)
    {
        var stopSequence = ReadStringProperty(source, "stop_sequence");
        return string.IsNullOrEmpty(stopSequence) ? null : stopSequence;
    }

    private static bool TryResolveAnthropicStopMetadata(
        string? data,
        out string stopReason,
        out string? stopSequence)
    {
        stopReason = "end_turn";
        stopSequence = null;
        if (string.IsNullOrWhiteSpace(data) ||
            string.Equals(data.Trim(), "[DONE]", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;
            if (TryResolveAnthropicDeltaStopMetadata(root, out stopReason, out stopSequence))
            {
                return true;
            }

            var source = root;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("response", out var response) &&
                response.ValueKind == JsonValueKind.Object)
            {
                source = response;
            }

            if (!HasAnthropicStopMetadata(root, source))
            {
                return false;
            }

            stopReason = ResolveAnthropicStopReason(source, hasToolUse: false);
            stopSequence = ResolveAnthropicStopSequence(source);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool HasAnthropicStopMetadata(JsonElement root, JsonElement source)
    {
        if (!string.IsNullOrWhiteSpace(ReadStringProperty(source, "stop_reason")) ||
            !string.IsNullOrEmpty(ResolveAnthropicStopSequence(source)) ||
            !string.IsNullOrWhiteSpace(TryReadStringPath(root, "delta", "stop_reason")) ||
            !string.IsNullOrWhiteSpace(TryReadStringPath(root, "delta", "stop_sequence")) ||
            !string.IsNullOrWhiteSpace(TryReadStringPath(source, "incomplete_details", "reason")) ||
            !string.IsNullOrWhiteSpace(TryReadStringPath(source, "choices", "0", "finish_reason")))
        {
            return true;
        }

        var type = ReadStringProperty(root, "type");
        return string.Equals(type, "response.completed", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(type, "response.incomplete", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryResolveAnthropicDeltaStopMetadata(
        JsonElement root,
        out string stopReason,
        out string? stopSequence)
    {
        stopReason = "end_turn";
        stopSequence = null;
        var deltaStopReason = TryReadStringPath(root, "delta", "stop_reason");
        var deltaStopSequence = TryReadStringPath(root, "delta", "stop_sequence");
        if (string.IsNullOrWhiteSpace(deltaStopReason) &&
            string.IsNullOrWhiteSpace(deltaStopSequence))
        {
            return false;
        }

        stopSequence = string.IsNullOrEmpty(deltaStopSequence) ? null : deltaStopSequence;
        stopReason = string.IsNullOrWhiteSpace(deltaStopReason)
            ? string.IsNullOrEmpty(stopSequence) ? "end_turn" : "stop_sequence"
            : MapResponsesStopReasonToAnthropicStopReason(deltaStopReason);
        return true;
    }

    private static string ReadStringProperty(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty(name, out var property) &&
                property.ValueKind == JsonValueKind.String)
            {
                return property.GetString() ?? string.Empty;
            }
        }

        return string.Empty;
    }

    private static bool TryReadResponsesStreamEvent(
        string? data,
        out string eventType,
        out string itemType,
        out string reasoningSignature)
    {
        eventType = string.Empty;
        itemType = string.Empty;
        reasoningSignature = string.Empty;
        if (string.IsNullOrWhiteSpace(data) ||
            string.Equals(data.Trim(), "[DONE]", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;
            eventType = ReadStringProperty(root, "type");
            if (!eventType.StartsWith("response.", StringComparison.OrdinalIgnoreCase))
            {
                eventType = string.Empty;
                return false;
            }

            if (root.TryGetProperty("item", out var item) &&
                item.ValueKind == JsonValueKind.Object)
            {
                itemType = ReadStringProperty(item, "type");
                reasoningSignature = ReadStringProperty(item, "encrypted_content", "signature");
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static bool ShouldSuppressVisibleDeltaAfterAnthropicToolUseEvent(
        string eventType,
        string itemType,
        int toolUseEventCount)
    {
        if (toolUseEventCount <= 0)
        {
            return false;
        }

        if (string.Equals(eventType, "response.function_call_arguments.delta", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(eventType, "response.function_call_arguments.done", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return (string.Equals(eventType, "response.output_item.added", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(eventType, "response.output_item.done", StringComparison.OrdinalIgnoreCase)) &&
               string.Equals(itemType, "function_call", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool ShouldSuppressVisibleDeltaAfterChatToolUseEvent(
        TransparentProxySseEvent sseEvent,
        int toolUseChunkCount)
    {
        if (toolUseChunkCount <= 0 ||
            !TryReadResponsesStreamEvent(sseEvent.Data, out var eventType, out var itemType, out _))
        {
            return false;
        }

        if (string.Equals(eventType, "response.function_call_arguments.delta", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(eventType, "response.function_call_arguments.done", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return (string.Equals(eventType, "response.output_item.added", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(eventType, "response.output_item.done", StringComparison.OrdinalIgnoreCase)) &&
               string.Equals(itemType, "function_call", StringComparison.OrdinalIgnoreCase);
    }

    internal static TransparentProxyAnthropicSseAggregate BuildAnthropicAggregateFromSseEvents(
        IEnumerable<TransparentProxySseEvent> events,
        string responseModel)
    {
        StringBuilder deltaText = new();
        StringBuilder pendingTextBlock = new();
        StringBuilder fallbackText = new();
        StringBuilder reasoningText = new();
        var reasoningSignature = string.Empty;
        var usage = TransparentProxyAnthropicUsage.Empty;
        var model = string.IsNullOrWhiteSpace(responseModel) ? string.Empty : responseModel.Trim();
        var stopReason = "end_turn";
        string? stopSequence = null;
        var contentCollector = new AnthropicSseContentBlockCollector();
        var toolUseNormalizer = new TransparentProxyAnthropicToolUseStreamNormalizer();

        void FlushReasoningBlock()
        {
            if (reasoningText.Length == 0 &&
                string.IsNullOrWhiteSpace(reasoningSignature))
            {
                return;
            }

            contentCollector.AddThinkingBlock(reasoningText.ToString(), reasoningSignature);
            reasoningText.Clear();
            reasoningSignature = string.Empty;
        }

        void FlushTextBlock()
        {
            if (pendingTextBlock.Length == 0)
            {
                return;
            }

            contentCollector.AddTextBlock(pendingTextBlock.ToString());
            pendingTextBlock.Clear();
        }

        foreach (var sseEvent in events)
        {
            var data = sseEvent.Data;
            if (string.IsNullOrWhiteSpace(data) ||
                string.Equals(data.Trim(), "[DONE]", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            usage = usage.Merge(ResolveAnthropicUsage(data, null));
            if (TryResolveAnthropicStopMetadata(data, out var eventStopReason, out var eventStopSequence))
            {
                stopReason = eventStopReason;
                stopSequence = eventStopSequence;
            }

            if (string.IsNullOrWhiteSpace(model))
            {
                var candidate = ResolveAnthropicModel(string.Empty, data);
                if (!string.Equals(candidate, "relaybench-proxy", StringComparison.OrdinalIgnoreCase))
                {
                    model = candidate;
                }
            }

            var toolUseEvents = toolUseNormalizer.ExtractToolUseEvents(data);
            if (toolUseEvents.Count > 0)
            {
                FlushReasoningBlock();
                FlushTextBlock();
            }

            foreach (var toolUseEvent in toolUseEvents)
            {
                contentCollector.Apply(toolUseEvent);
            }

            if (contentCollector.Apply(sseEvent))
            {
                continue;
            }

            var isResponsesStreamEvent = TryReadResponsesStreamEvent(
                data,
                out var responseEventType,
                out var responseItemType,
                out var responseReasoningSignature);
            if (isResponsesStreamEvent &&
                string.Equals(responseItemType, "reasoning", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(responseReasoningSignature))
                {
                    reasoningSignature = responseReasoningSignature;
                }

                if (string.Equals(responseEventType, "response.output_item.done", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            if (ShouldSuppressVisibleDeltaAfterAnthropicToolUseEvent(
                    responseEventType,
                    responseItemType,
                    toolUseEvents.Count))
            {
                continue;
            }

            var reasoningDelta = ChatSseParser.TryExtractReasoningDelta(data);
            if (!string.IsNullOrEmpty(reasoningDelta))
            {
                FlushTextBlock();
                reasoningText.Append(reasoningDelta);
                continue;
            }

            var delta = ChatSseParser.TryExtractDelta(data);
            if (!string.IsNullOrEmpty(delta))
            {
                FlushReasoningBlock();
                pendingTextBlock.Append(delta);
                deltaText.Append(delta);
                continue;
            }

            if (IsAggregateFallbackEvent(sseEvent) &&
                TryExtractAssistantTextFromSseData(data) is { Length: > 0 } fallback)
            {
                fallbackText.Append(fallback);
            }
        }

        foreach (var toolUseEvent in toolUseNormalizer.FlushOpenBlocks())
        {
            contentCollector.Apply(toolUseEvent);
        }

        if (deltaText.Length == 0 && fallbackText.Length > 0)
        {
            pendingTextBlock.Append(fallbackText.ToString());
        }

        FlushReasoningBlock();
        FlushTextBlock();

        var content = contentCollector.BuildContent();
        EnsureAnthropicAggregateToolUseIdPrefix(content);
        var assistantText = deltaText.Length > 0
            ? deltaText.ToString()
            : contentCollector.VisibleText.Length > 0
                ? contentCollector.VisibleText
                : fallbackText.ToString();
        return new TransparentProxyAnthropicSseAggregate(
            assistantText,
            string.IsNullOrWhiteSpace(model) ? "relaybench-proxy" : model,
            usage.WithOutputFallback(TokenCountEstimator.EstimateOutputTokens(assistantText)),
            stopReason,
            stopSequence,
            content.Count > 0 ? content : null);
    }

    private static void EnsureAnthropicAggregateToolUseIdPrefix(JsonArray content)
    {
        foreach (var node in content)
        {
            if (node is not JsonObject block ||
                !string.Equals(ReadStringNode(block["type"]), "tool_use", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var id = TransparentProxyClaudeToolUseId.Normalize(ReadStringNode(block["id"]));
            block["id"] = id.StartsWith("toolu_", StringComparison.Ordinal) ? id : "toolu_" + id;
        }
    }

    private static string ReadStringNode(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : string.Empty;

    private sealed class AnthropicSseContentBlockCollector
    {
        private readonly SortedDictionary<int, BlockState> _openBlocks = new();
        private readonly JsonArray _content = [];
        private readonly StringBuilder _visibleText = new();

        public string VisibleText => _visibleText.ToString();

        public bool Apply(TransparentProxySseEvent sseEvent)
        {
            if (string.IsNullOrWhiteSpace(sseEvent.Data) ||
                string.Equals(sseEvent.Data.Trim(), "[DONE]", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            try
            {
                using var document = JsonDocument.Parse(sseEvent.Data);
                var root = document.RootElement;
                var type = ReadStringProperty(root, "type");
                if (string.IsNullOrWhiteSpace(type))
                {
                    type = sseEvent.EventName;
                }

                if (string.Equals(type, "content_block_start", StringComparison.OrdinalIgnoreCase))
                {
                    ApplyStart(root);
                    return true;
                }

                if (string.Equals(type, "content_block_delta", StringComparison.OrdinalIgnoreCase))
                {
                    ApplyDelta(root);
                    return true;
                }

                if (string.Equals(type, "content_block_stop", StringComparison.OrdinalIgnoreCase))
                {
                    ApplyStop(root);
                    return true;
                }
            }
            catch (JsonException)
            {
            }

            return false;
        }

        public void AddTextBlock(string text)
        {
            _content.Add(new JsonObject
            {
                ["type"] = "text",
                ["text"] = text
            });
            _visibleText.Append(text);
        }

        public void AddThinkingBlock(string thinking, string signature)
        {
            var block = new JsonObject
            {
                ["type"] = "thinking",
                ["thinking"] = thinking
            };
            if (!string.IsNullOrWhiteSpace(signature))
            {
                block["signature"] = signature;
            }

            _content.Add(block);
        }

        public JsonArray BuildContent()
        {
            foreach (var index in _openBlocks.Keys.ToArray())
            {
                FinalizeBlock(index);
            }

            return _content;
        }

        private void ApplyStart(JsonElement root)
        {
            var index = ReadContentBlockIndex(root);
            var state = GetState(index);
            if (!root.TryGetProperty("content_block", out var block) ||
                block.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            state.Type = ReadStringProperty(block, "type");
            if (string.Equals(state.Type, "tool_use", StringComparison.OrdinalIgnoreCase))
            {
                state.Id = ReadStringProperty(block, "id");
                state.Name = ReadStringProperty(block, "name");
                if (block.TryGetProperty("input", out var input) &&
                    input.ValueKind == JsonValueKind.Object &&
                    HasObjectProperties(input))
                {
                    state.InputJson.Append(input.GetRawText());
                }
            }
            else if (string.Equals(state.Type, "text", StringComparison.OrdinalIgnoreCase))
            {
                state.Text.Append(ReadStringProperty(block, "text"));
            }
            else if (string.Equals(state.Type, "thinking", StringComparison.OrdinalIgnoreCase))
            {
                state.Thinking.Append(ReadStringProperty(block, "thinking"));
            }
        }

        private void ApplyDelta(JsonElement root)
        {
            var index = ReadContentBlockIndex(root);
            var state = GetState(index);
            if (!root.TryGetProperty("delta", out var delta) ||
                delta.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var deltaType = ReadStringProperty(delta, "type");
            if (string.Equals(deltaType, "text_delta", StringComparison.OrdinalIgnoreCase))
            {
                state.Type = string.IsNullOrWhiteSpace(state.Type) ? "text" : state.Type;
                state.Text.Append(ReadStringProperty(delta, "text"));
            }
            else if (string.Equals(deltaType, "thinking_delta", StringComparison.OrdinalIgnoreCase))
            {
                state.Type = string.IsNullOrWhiteSpace(state.Type) ? "thinking" : state.Type;
                state.Thinking.Append(ReadStringProperty(delta, "thinking"));
            }
            else if (string.Equals(deltaType, "signature_delta", StringComparison.OrdinalIgnoreCase))
            {
                state.Type = string.IsNullOrWhiteSpace(state.Type) ? "thinking" : state.Type;
                state.Signature = ReadStringProperty(delta, "signature");
            }
            else if (string.Equals(deltaType, "input_json_delta", StringComparison.OrdinalIgnoreCase))
            {
                state.Type = string.IsNullOrWhiteSpace(state.Type) ? "tool_use" : state.Type;
                state.InputJson.Append(ReadStringProperty(delta, "partial_json"));
            }
        }

        private void ApplyStop(JsonElement root)
            => FinalizeBlock(ReadContentBlockIndex(root));

        private BlockState GetState(int index)
        {
            if (!_openBlocks.TryGetValue(index, out var state))
            {
                state = new BlockState();
                _openBlocks[index] = state;
            }

            return state;
        }

        private void FinalizeBlock(int index)
        {
            if (!_openBlocks.Remove(index, out var state))
            {
                return;
            }

            if (string.Equals(state.Type, "tool_use", StringComparison.OrdinalIgnoreCase))
            {
                _content.Add(new JsonObject
                {
                    ["type"] = "tool_use",
                    ["id"] = TransparentProxyClaudeToolUseId.Normalize(state.Id),
                    ["name"] = string.IsNullOrWhiteSpace(state.Name) ? "tool" : state.Name,
                    ["input"] = ParseAnthropicToolInput(state.InputJson.ToString())
                });
                return;
            }

            if (string.Equals(state.Type, "thinking", StringComparison.OrdinalIgnoreCase))
            {
                AddThinkingBlock(state.Thinking.ToString(), state.Signature ?? string.Empty);
                return;
            }

            if (string.Equals(state.Type, "text", StringComparison.OrdinalIgnoreCase))
            {
                AddTextBlock(state.Text.ToString());
            }
        }

        private static int ReadContentBlockIndex(JsonElement root)
            => root.TryGetProperty("index", out var indexElement) &&
               indexElement.ValueKind == JsonValueKind.Number &&
               indexElement.TryGetInt32(out var index)
                ? Math.Max(0, index)
                : 0;

        private static bool HasObjectProperties(JsonElement element)
        {
            foreach (var _ in element.EnumerateObject())
            {
                return true;
            }

            return false;
        }

        private sealed class BlockState
        {
            public string Type { get; set; } = string.Empty;
            public string Id { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public StringBuilder Text { get; } = new();
            public StringBuilder Thinking { get; } = new();
            public StringBuilder InputJson { get; } = new();
            public string? Signature { get; set; }
        }
    }

    private static bool IsAggregateFallbackEvent(TransparentProxySseEvent sseEvent)
    {
        if (sseEvent.EventName.Equals("response.output_item.done", StringComparison.OrdinalIgnoreCase) ||
            sseEvent.EventName.Equals("response.completed", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(sseEvent.Data);
            return document.RootElement.TryGetProperty("type", out var type) &&
                   type.ValueKind == JsonValueKind.String &&
                   (type.GetString()?.Equals("response.output_item.done", StringComparison.OrdinalIgnoreCase) == true ||
                    type.GetString()?.Equals("response.completed", StringComparison.OrdinalIgnoreCase) == true);
        }
        catch
        {
            return false;
        }
    }

    private static string? TryExtractAssistantTextFromSseData(string data)
    {
        try
        {
            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;
            if (root.TryGetProperty("response", out var response))
            {
                return ModelResponseTextExtractor.TryExtractAssistantText(response);
            }

            if (root.TryGetProperty("item", out var item))
            {
                return ModelResponseTextExtractor.TryExtractAssistantText(item);
            }

            return ModelResponseTextExtractor.TryExtractAssistantText(root);
        }
        catch
        {
            return null;
        }
    }

    private static string BuildAnthropicMessageStartEvent(string messageId, string model)
        => JsonSerializer.Serialize(new
        {
            type = "message_start",
            message = new
            {
                id = messageId,
                type = "message",
                role = "assistant",
                model,
                content = Array.Empty<object>(),
                stop_reason = (string?)null,
                stop_sequence = (string?)null,
                usage = TransparentProxyAnthropicUsage.Empty.EnsureMessageUsage()
            }
        });

    private static string BuildAnthropicTextContentBlockStartEvent(int index)
        => JsonSerializer.Serialize(new
        {
            type = "content_block_start",
            index,
            content_block = new
            {
                type = "text",
                text = string.Empty
            }
        });

    internal static string BuildAnthropicThinkingContentBlockStartEvent(int index)
        => JsonSerializer.Serialize(new
        {
            type = "content_block_start",
            index,
            content_block = new
            {
                type = "thinking",
                thinking = string.Empty
            }
        });

    internal static string BuildAnthropicThinkingDeltaEvent(string delta, int index)
        => JsonSerializer.Serialize(new
        {
            type = "content_block_delta",
            index,
            delta = new
            {
                type = "thinking_delta",
                thinking = delta
            }
        });

    internal static string BuildAnthropicSignatureDeltaEvent(string signature, int index)
        => JsonSerializer.Serialize(new
        {
            type = "content_block_delta",
            index,
            delta = new
            {
                type = "signature_delta",
                signature
            }
        });

    internal static string BuildAnthropicToolUseContentBlockStartEvent(
        int index,
        string id,
        string name)
        => JsonSerializer.Serialize(new
        {
            type = "content_block_start",
            index,
            content_block = new
            {
                type = "tool_use",
                id,
                name,
                input = new { }
            }
        });

    internal static string BuildAnthropicToolUseInputDeltaEvent(string partialJson, int index)
        => JsonSerializer.Serialize(new
        {
            type = "content_block_delta",
            index,
            delta = new
            {
                type = "input_json_delta",
                partial_json = partialJson
            }
        });

    private static string BuildAnthropicContentDeltaEvent(string delta)
        => BuildAnthropicContentDeltaEvent(delta, 0);

    private static string BuildAnthropicContentDeltaEvent(string delta, int index)
        => JsonSerializer.Serialize(new
        {
            type = "content_block_delta",
            index,
            delta = new
            {
                type = "text_delta",
                text = delta
            }
        });

    private static string BuildAnthropicContentBlockStopEvent(int index)
        => JsonSerializer.Serialize(new
        {
            type = "content_block_stop",
            index
        });

    internal static string BuildAnthropicMessageDeltaEvent(
        TransparentProxyAnthropicUsage usage,
        string stopReason = "end_turn",
        string? stopSequence = null)
        => JsonSerializer.Serialize(new
        {
            type = "message_delta",
            delta = new
            {
                stop_reason = stopReason,
                stop_sequence = string.IsNullOrEmpty(stopSequence) ? null : stopSequence
            },
            usage = usage.EnsureDeltaUsage()
        });

    private static string BuildAnthropicMessageDeltaEvent(int outputTokens)
        => BuildAnthropicMessageDeltaEvent(TransparentProxyAnthropicUsage.FromOutput(outputTokens));

    private static string ResolveAnthropicModel(string responseModel, string upstreamText)
    {
        if (!string.IsNullOrWhiteSpace(responseModel))
        {
            return responseModel.Trim();
        }

        try
        {
            using var document = JsonDocument.Parse(upstreamText);
            return TryReadStringPath(document.RootElement, "model") ??
                   TryReadStringPath(document.RootElement, "response", "model") ??
                   "relaybench-proxy";
        }
        catch
        {
            return "relaybench-proxy";
        }
    }

    internal static TransparentProxyAnthropicUsage ResolveAnthropicUsage(string? upstreamText, string? assistantText)
    {
        var inputTokens = 0;
        var estimatedOutputTokens = Math.Max(0, TokenCountEstimator.EstimateOutputTokens(assistantText));
        var outputTokens = estimatedOutputTokens;
        var cacheReadInputTokens = 0;
        var cacheCreationInputTokens = 0;
        var cacheReadFromOpenAiDetails = false;
        try
        {
            if (string.IsNullOrWhiteSpace(upstreamText))
            {
                return new TransparentProxyAnthropicUsage(0, outputTokens);
            }

            using var document = JsonDocument.Parse(upstreamText);
            if (TryResolveUsageElement(document.RootElement, out var usage))
            {
                inputTokens = ReadFirstUsageToken(
                    usage,
                    "input_tokens",
                    "inputTokens",
                    "prompt_tokens",
                    "promptTokens");
                outputTokens = Math.Max(
                    estimatedOutputTokens,
                    ReadFirstUsageToken(
                        usage,
                        "output_tokens",
                        "outputTokens",
                        "completion_tokens",
                        "completionTokens",
                        "generated_tokens"));
                if (!TryReadFirstIntProperty(usage, out cacheReadInputTokens, "cache_read_input_tokens", "cacheReadInputTokens") &&
                    !TryReadFirstIntProperty(usage, out cacheReadInputTokens, "cached_tokens", "cachedTokens"))
                {
                    cacheReadInputTokens = Math.Max(
                        Math.Max(
                            TryReadIntPath(usage, "input_tokens_details", "cached_tokens"),
                            TryReadIntPath(usage, "prompt_tokens_details", "cached_tokens")),
                        TryReadIntPath(usage, "inputTokenDetails", "cachedTokens"));
                    cacheReadFromOpenAiDetails = cacheReadInputTokens > 0;
                }

                cacheCreationInputTokens = ReadFirstUsageToken(
                    usage,
                    "cache_creation_input_tokens",
                    "cacheCreationInputTokens");
            }
            else if (TryResolveUsageMetadataElement(document.RootElement, out var usageMetadata))
            {
                inputTokens = TryReadIntProperty(usageMetadata, "promptTokenCount");
                outputTokens = Math.Max(outputTokens, TryReadIntProperty(usageMetadata, "candidatesTokenCount"));
                cacheReadInputTokens = TryReadIntProperty(usageMetadata, "cachedContentTokenCount");
            }
        }
        catch
        {
        }

        if (cacheReadFromOpenAiDetails && cacheReadInputTokens > 0)
        {
            inputTokens = Math.Max(0, inputTokens - cacheReadInputTokens);
        }

        return new TransparentProxyAnthropicUsage(
            Math.Max(0, inputTokens),
            Math.Max(0, outputTokens),
            cacheReadInputTokens > 0 ? cacheReadInputTokens : null,
            cacheCreationInputTokens > 0 ? cacheCreationInputTokens : null);
    }

    private static bool TryResolveUsageElement(JsonElement root, out JsonElement usage)
    {
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("usage", out usage) &&
            usage.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("response", out var response) &&
            response.ValueKind == JsonValueKind.Object &&
            response.TryGetProperty("usage", out usage) &&
            usage.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        usage = default;
        return false;
    }

    private static bool TryResolveUsageMetadataElement(JsonElement root, out JsonElement usageMetadata)
    {
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("usageMetadata", out usageMetadata) &&
            usageMetadata.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("usage_metadata", out usageMetadata) &&
            usageMetadata.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("response", out var response) &&
            response.ValueKind == JsonValueKind.Object &&
            response.TryGetProperty("usageMetadata", out usageMetadata) &&
            usageMetadata.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("response", out response) &&
            response.ValueKind == JsonValueKind.Object &&
            response.TryGetProperty("usage_metadata", out usageMetadata) &&
            usageMetadata.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        usageMetadata = default;
        return false;
    }

    private static int TryReadIntProperty(JsonElement root, string propertyName)
        => root.ValueKind == JsonValueKind.Object &&
           root.TryGetProperty(propertyName, out var property)
            ? property.ValueKind switch
            {
                JsonValueKind.Number when property.TryGetInt32(out var number) => Math.Max(0, number),
                JsonValueKind.String when int.TryParse(property.GetString(), out var number) => Math.Max(0, number),
                _ => 0
            }
            : 0;

    private static int ReadFirstUsageToken(JsonElement root, params string[] propertyNames)
        => TryReadFirstIntProperty(root, out var value, propertyNames) ? value : 0;

    private static bool TryReadFirstIntProperty(JsonElement root, out int value, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (TryReadOptionalIntProperty(root, propertyName, out value))
            {
                return true;
            }
        }

        value = 0;
        return false;
    }

    private static bool TryReadOptionalIntProperty(JsonElement root, string propertyName, out int value)
    {
        value = 0;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        switch (property.ValueKind)
        {
            case JsonValueKind.Number when property.TryGetInt32(out var number):
                value = Math.Max(0, number);
                return true;
            case JsonValueKind.String when int.TryParse(property.GetString(), out var number):
                value = Math.Max(0, number);
                return true;
            default:
                return false;
        }
    }

    private static int TryReadIntPath(JsonElement root, params string[] path)
    {
        var current = root;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object ||
                !current.TryGetProperty(segment, out current))
            {
                return 0;
            }
        }

        return current.ValueKind switch
        {
            JsonValueKind.Number when current.TryGetInt32(out var number) => Math.Max(0, number),
            JsonValueKind.String when int.TryParse(current.GetString(), out var number) => Math.Max(0, number),
            _ => 0
        };
    }

    private static string? TryReadStringPath(JsonElement root, params string[] path)
    {
        var current = root;
        foreach (var segment in path)
        {
            if (current.ValueKind == JsonValueKind.Object &&
                current.TryGetProperty(segment, out current))
            {
                continue;
            }

            if (current.ValueKind == JsonValueKind.Array &&
                int.TryParse(segment, out var index) &&
                index >= 0 &&
                index < current.GetArrayLength())
            {
                current = current[index];
                continue;
            }

            return null;
        }

        return current.ValueKind == JsonValueKind.String
            ? current.GetString()
            : null;
    }

    private static void CopyResponseHeaders(HttpResponseMessage upstreamResponse, HttpListenerResponse response)
    {
        var connectionScopedHeaders = TransparentProxyHeaderFilter.ResolveConnectionScopedHeaders(upstreamResponse.Headers.Connection);
        foreach (var header in upstreamResponse.Headers)
        {
            if (!TransparentProxyHeaderFilter.ShouldSkipForwardedResponseHeader(header.Key, connectionScopedHeaders))
            {
                TrySetResponseHeader(response, header.Key, header.Value);
            }
        }

        foreach (var header in upstreamResponse.Content.Headers)
        {
            if (!TransparentProxyHeaderFilter.ShouldSkipForwardedResponseHeader(header.Key, connectionScopedHeaders) &&
                !string.Equals(header.Key, "content-type", StringComparison.OrdinalIgnoreCase))
            {
                TrySetResponseHeader(response, header.Key, header.Value);
            }
        }
    }

    private static void ClearTransformedResponseHeaders(HttpListenerResponse response)
    {
        foreach (var headerName in new[] { "Content-Encoding", "Content-MD5", "Content-Range" })
        {
            try
            {
                response.Headers.Remove(headerName);
            }
            catch
            {
            }
        }
    }

    private static void TrySetResponseHeader(HttpListenerResponse response, string name, IEnumerable<string> values)
    {
        try
        {
            response.Headers[name] = string.Join(",", values);
        }
        catch
        {
            // Some framework-managed headers cannot be set directly; keep proxying.
        }
    }

    private static void AddCorsHeaders(HttpListenerResponse response)
    {
        response.Headers["Access-Control-Allow-Origin"] = "*";
        response.Headers["Access-Control-Allow-Headers"] = "authorization, content-type, x-api-key, anthropic-version, anthropic-beta, openai-beta, idempotency-key, session_id";
        response.Headers["Access-Control-Allow-Methods"] = "GET, POST, PUT, PATCH, DELETE, OPTIONS";
        response.Headers["X-RelayBench-Proxy"] = "transparent";
    }

    private static string NormalizeLogModel(string? modelName)
        => string.IsNullOrWhiteSpace(modelName) ? "-" : modelName.Trim();
}

internal sealed record TransparentProxyAnthropicUsage(
    [property: JsonPropertyName("input_tokens")]
    int InputTokens,
    [property: JsonPropertyName("output_tokens")]
    int OutputTokens,
    [property: JsonPropertyName("cache_read_input_tokens")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? CacheReadInputTokens = null,
    [property: JsonPropertyName("cache_creation_input_tokens")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? CacheCreationInputTokens = null)
{
    public static TransparentProxyAnthropicUsage Empty { get; } = new(0, 0);

    public static TransparentProxyAnthropicUsage FromOutput(int outputTokens)
        => new(0, Math.Max(0, outputTokens));

    public TransparentProxyAnthropicUsage EnsureMessageUsage()
        => this with
        {
            InputTokens = Math.Max(0, InputTokens),
            OutputTokens = Math.Max(0, OutputTokens)
        };

    public TransparentProxyAnthropicDeltaUsage EnsureDeltaUsage()
        => new(
            Math.Max(0, OutputTokens),
            InputTokens > 0 ? InputTokens : null,
            CacheReadInputTokens is > 0 ? CacheReadInputTokens : null,
            CacheCreationInputTokens is > 0 ? CacheCreationInputTokens : null);

    public TransparentProxyAnthropicUsage Merge(TransparentProxyAnthropicUsage other)
        => new(
            Math.Max(InputTokens, other.InputTokens),
            Math.Max(OutputTokens, other.OutputTokens),
            MaxNullable(CacheReadInputTokens, other.CacheReadInputTokens),
            MaxNullable(CacheCreationInputTokens, other.CacheCreationInputTokens));

    public TransparentProxyAnthropicUsage WithOutputFallback(int outputTokens)
        => this with
        {
            OutputTokens = OutputTokens > 0 ? OutputTokens : Math.Max(0, outputTokens)
        };

    private static int? MaxNullable(int? left, int? right)
    {
        var value = Math.Max(left ?? 0, right ?? 0);
        return value > 0 ? value : null;
    }
}

internal sealed record TransparentProxyAnthropicDeltaUsage(
    [property: JsonPropertyName("output_tokens")]
    int OutputTokens,
    [property: JsonPropertyName("input_tokens")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? InputTokens = null,
    [property: JsonPropertyName("cache_read_input_tokens")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? CacheReadInputTokens = null,
    [property: JsonPropertyName("cache_creation_input_tokens")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? CacheCreationInputTokens = null);

internal sealed record TransparentProxyAnthropicSseAggregate(
    string AssistantText,
    string Model,
    TransparentProxyAnthropicUsage Usage,
    string StopReason = "end_turn",
    string? StopSequence = null,
    JsonArray? Content = null);

internal sealed record TransparentProxyChatSseAggregate(
    string AssistantText,
    string Model,
    System.Text.Json.Nodes.JsonNode? Usage,
    string ReasoningText,
    IReadOnlyList<TransparentProxyResponseNormalizationService.TransparentProxyNormalizedToolCall> ToolCalls,
    IReadOnlyList<TransparentProxyResponseNormalizationService.TransparentProxyNormalizedImage> Images);
